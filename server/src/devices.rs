//! Which devices may use this server. A device pairs once using the server's
//! pairing key and gets its own token. Only a hash of each token is stored, so
//! reading the database doesn't reveal any logins.

use std::sync::Arc;
use std::time::{Duration, Instant};

use axum::{
    Json,
    extract::{Path, State},
    http::{HeaderMap, StatusCode, header::AUTHORIZATION},
};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use subtle::ConstantTimeEq;
use tokio_postgres::Client;

use crate::AppState;

const MAX_NAME_LENGTH: usize = 64;
const MAX_FAILED_PAIRINGS: u32 = 5;
const FAILED_PAIRING_WINDOW: Duration = Duration::from_secs(60);

/// Counts wrong pairing keys across all callers. Requests all arrive through a
/// local tunnel, so there's no caller address to count per client.
#[derive(Default)]
pub struct PairingLimit {
    window_started: Option<Instant>,
    failures: u32,
}

impl PairingLimit {
    fn is_blocked(&mut self) -> bool {
        if self
            .window_started
            .is_some_and(|started| started.elapsed() >= FAILED_PAIRING_WINDOW)
        {
            *self = Self::default();
        }
        self.failures >= MAX_FAILED_PAIRINGS
    }

    fn record_failure(&mut self) {
        self.window_started.get_or_insert_with(Instant::now);
        self.failures += 1;
    }
}

pub async fn create_tables(db: &Client) -> Result<(), tokio_postgres::Error> {
    db.batch_execute(
        "CREATE TABLE IF NOT EXISTS devices (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            token_hash TEXT NOT NULL UNIQUE,
            created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
            last_seen_at TIMESTAMPTZ NOT NULL DEFAULT now()
        )",
    )
    .await
}

#[derive(Deserialize)]
pub struct PairRequest {
    key: String,
    name: String,
}

#[derive(Serialize)]
pub struct PairResponse {
    id: String,
    token: String,
}

pub async fn pair(
    State(state): State<Arc<AppState>>,
    Json(request): Json<PairRequest>,
) -> Result<Json<PairResponse>, StatusCode> {
    let expected = state
        .pairing_key
        .as_deref()
        .ok_or(StatusCode::SERVICE_UNAVAILABLE)?;
    let name = clean_name(&request.name)?;

    {
        let mut limit = state.pairing_limit.lock().unwrap();
        if limit.is_blocked() {
            return Err(StatusCode::TOO_MANY_REQUESTS);
        }
        let matches: bool = sha256(&request.key)
            .as_bytes()
            .ct_eq(sha256(expected).as_bytes())
            .into();
        if !matches {
            limit.record_failure();
            return Err(StatusCode::UNAUTHORIZED);
        }
    }

    let id = random_hex::<16>();
    let token = random_hex::<32>();
    state
        .db
        .execute(
            "INSERT INTO devices (id, name, token_hash) VALUES ($1, $2, $3)",
            &[&id, &name, &sha256(&token)],
        )
        .await
        .map_err(internal_error)?;

    Ok(Json(PairResponse { id, token }))
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DeviceView {
    id: String,
    name: String,
    current: bool,
    last_seen_at: f64,
}

pub async fn list(
    State(state): State<Arc<AppState>>,
    headers: HeaderMap,
) -> Result<Json<Vec<DeviceView>>, StatusCode> {
    let current = authenticate(&state, &headers).await?;
    let rows = state
        .db
        .query(
            "SELECT id, name, EXTRACT(EPOCH FROM last_seen_at)::float8
             FROM devices ORDER BY created_at",
            &[],
        )
        .await
        .map_err(internal_error)?;

    // Removed by another PC between those two steps, so this login is no longer good.
    if !rows.iter().any(|row| row.get::<_, String>(0) == current) {
        return Err(StatusCode::UNAUTHORIZED);
    }

    Ok(Json(
        rows.iter()
            .map(|row| {
                let id: String = row.get(0);
                DeviceView {
                    current: id == current,
                    name: row.get(1),
                    last_seen_at: row.get(2),
                    id,
                }
            })
            .collect(),
    ))
}

#[derive(Deserialize)]
pub struct RenameRequest {
    name: String,
}

pub async fn rename(
    State(state): State<Arc<AppState>>,
    headers: HeaderMap,
    Path(id): Path<String>,
    Json(request): Json<RenameRequest>,
) -> Result<StatusCode, StatusCode> {
    authenticate(&state, &headers).await?;
    let name = clean_name(&request.name)?;
    let changed = state
        .db
        .execute("UPDATE devices SET name = $1 WHERE id = $2", &[&name, &id])
        .await
        .map_err(internal_error)?;
    found_or_404(changed)
}

/// Removing a device ends its access. A device may remove itself to sign out.
pub async fn remove(
    State(state): State<Arc<AppState>>,
    headers: HeaderMap,
    Path(id): Path<String>,
) -> Result<StatusCode, StatusCode> {
    authenticate(&state, &headers).await?;
    let removed = state
        .db
        .execute("DELETE FROM devices WHERE id = $1", &[&id])
        .await
        .map_err(internal_error)?;
    found_or_404(removed)
}

pub async fn authenticate(state: &AppState, headers: &HeaderMap) -> Result<String, StatusCode> {
    let token = headers
        .get(AUTHORIZATION)
        .and_then(|value| value.to_str().ok())
        .and_then(|value| value.strip_prefix("Bearer "))
        .ok_or(StatusCode::UNAUTHORIZED)?;
    let row = state
        .db
        .query_opt(
            "UPDATE devices SET last_seen_at = now() WHERE token_hash = $1 RETURNING id",
            &[&sha256(token)],
        )
        .await
        .map_err(internal_error)?
        .ok_or(StatusCode::UNAUTHORIZED)?;
    Ok(row.get(0))
}

fn clean_name(name: &str) -> Result<String, StatusCode> {
    let name = name.trim();
    if name.is_empty() || name.chars().count() > MAX_NAME_LENGTH {
        return Err(StatusCode::BAD_REQUEST);
    }
    Ok(name.to_string())
}

fn found_or_404(rows: u64) -> Result<StatusCode, StatusCode> {
    if rows == 0 {
        Err(StatusCode::NOT_FOUND)
    } else {
        Ok(StatusCode::NO_CONTENT)
    }
}

pub fn internal_error(error: tokio_postgres::Error) -> StatusCode {
    eprintln!("Database error: {error}");
    StatusCode::INTERNAL_SERVER_ERROR
}

fn sha256(text: &str) -> String {
    hex::encode(Sha256::digest(text))
}

fn random_hex<const N: usize>() -> String {
    let mut bytes = [0u8; N];
    getrandom::fill(&mut bytes).expect("the system's random source failed");
    hex::encode(bytes)
}
