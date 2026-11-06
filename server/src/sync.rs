//! Keeps every device's data in step. The server stores records as opaque JSON
//! text and never reads inside them, so the data's shape can change freely
//! without changes here.

use std::sync::Arc;

use axum::{
    Json,
    extract::{State, rejection::JsonRejection},
    http::{HeaderMap, StatusCode},
};
use serde::{Deserialize, Serialize};
use tokio_postgres::Client;

use crate::{
    AppState, VERSION,
    devices::{authenticate, internal_error},
};

const MAX_CHANGES_PER_REQUEST: usize = 500;
const MAX_KEY_LENGTH: usize = 128;
const PAGE_SIZE: i64 = 500;
const MAX_RESPONSE_BYTES: usize = 1_500_000;

pub async fn create_tables(db: &Client) -> Result<(), tokio_postgres::Error> {
    db.batch_execute(
        "CREATE SEQUENCE IF NOT EXISTS sync_seq;
         CREATE TABLE IF NOT EXISTS sync_records (
            collection TEXT NOT NULL,
            id TEXT NOT NULL,
            data TEXT NOT NULL,
            deleted BOOLEAN NOT NULL,
            seq BIGINT NOT NULL,
            updated_by TEXT NOT NULL,
            PRIMARY KEY (collection, id)
         );
         ALTER TABLE sync_records ADD COLUMN IF NOT EXISTS changed_at BIGINT NOT NULL DEFAULT 0;
         CREATE INDEX IF NOT EXISTS sync_records_seq ON sync_records (seq);
         CREATE TABLE IF NOT EXISTS sync_meta (epoch TEXT NOT NULL);
         INSERT INTO sync_meta (epoch)
            SELECT md5(random()::text || clock_timestamp()::text)
            WHERE NOT EXISTS (SELECT 1 FROM sync_meta);",
    )
    .await
}

#[derive(Serialize, Deserialize)]
pub struct Change {
    collection: String,
    id: String,
    data: String,
    deleted: bool,
    /// When the device made this copy, in milliseconds.
    #[serde(default, rename = "changedAt")]
    changed_at: i64,
}

#[derive(Deserialize)]
pub struct SyncRequest {
    cursor: i64,
    changes: Vec<Change>,
    /// Which data the device believes it's up to date with.
    #[serde(default)]
    epoch: Option<String>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SyncResponse {
    cursor: i64,
    has_more: bool,
    changes: Vec<Change>,
    /// Made once with the database. A new one tells a device this server has lost
    /// what it had, so the device's cursor means nothing and it should send everything.
    epoch: String,
}

/// Nothing stays compatible across a release, so only this server's own can read or change data
/// here. Changes stay queued on a PC until the two match, which takes minutes at most after a
/// release, since both update themselves. Dev builds don't send a version.
fn client_is_out_of_date(headers: &HeaderMap) -> bool {
    let parts = |version: &str| -> Option<Vec<u64>> {
        version.split('.').map(|part| part.parse().ok()).collect()
    };
    headers
        .get("x-agent-orange-version")
        .and_then(|value| value.to_str().ok())
        .and_then(parts)
        .zip(parts(VERSION))
        .is_some_and(|(client, server)| client != server)
}

/// Saves this device's changes, then returns everything newer than its cursor. The newest edit of
/// each record wins, whatever order they arrive in, so a PC coming back online can't undo what
/// others did while it was away. A device drops a queued edit that's older than a copy it gets from
/// here, so it can't end up holding on to one that was refused.
///
/// The server has one database connection, so writes run one at a time and
/// sequence numbers become visible in order. A cursor can't skip past a change
/// that's still being written. More connections would need this rethinking.
pub async fn sync(
    State(state): State<Arc<AppState>>,
    headers: HeaderMap,
    request: Result<Json<SyncRequest>, JsonRejection>,
) -> Result<Json<SyncResponse>, StatusCode> {
    let device = authenticate(&state, &headers).await?;

    // Before reading the request, because another release's may not even parse. After the login,
    // because only paired devices are told anything about this server's version.
    if client_is_out_of_date(&headers) {
        return Err(StatusCode::UPGRADE_REQUIRED);
    }
    let Json(request) = request.map_err(|rejection| rejection.status())?;

    if request.changes.len() > MAX_CHANGES_PER_REQUEST {
        return Err(StatusCode::PAYLOAD_TOO_LARGE);
    }

    // A device ahead of everything here, on what it thinks is this same data, means this
    // database was restored from a backup. It would otherwise miss changes without any sign
    // until the numbers caught up, so this counts as new data and devices send theirs again.
    if let Some(device_epoch) = &request.epoch {
        let current = state
            .db
            .query_one(
                "SELECT epoch, (SELECT COALESCE(MAX(seq), 0) FROM sync_records) FROM sync_meta",
                &[],
            )
            .await
            .map_err(internal_error)?;
        let current_epoch: String = current.get(0);
        let latest: i64 = current.get(1);
        if *device_epoch == current_epoch && request.cursor > latest {
            state
                .db
                .execute(
                    "UPDATE sync_meta SET epoch = md5(random()::text || clock_timestamp()::text)",
                    &[],
                )
                .await
                .map_err(internal_error)?;
        }
    }

    for change in &request.changes {
        if change.collection.is_empty()
            || change.id.is_empty()
            || change.collection.len() > MAX_KEY_LENGTH
            || change.id.len() > MAX_KEY_LENGTH
            || [&change.collection, &change.id, &change.data]
                .iter()
                .any(|text| text.contains('\0'))
        // Postgres can't store this, and a 500 would be retried forever.
        {
            return Err(StatusCode::BAD_REQUEST);
        }

        state
            .db
            .execute(
                "INSERT INTO sync_records (collection, id, data, deleted, seq, updated_by, changed_at)
                 VALUES ($1, $2, $3, $4, nextval('sync_seq'), $5, $6)
                 ON CONFLICT (collection, id) DO UPDATE SET
                    data = excluded.data,
                    deleted = excluded.deleted,
                    seq = excluded.seq,
                    updated_by = excluded.updated_by,
                    changed_at = excluded.changed_at
                 WHERE excluded.changed_at > sync_records.changed_at
                    OR (excluded.changed_at = sync_records.changed_at
                        AND (excluded.data <> sync_records.data OR excluded.deleted <> sync_records.deleted))",
                &[
                    &change.collection,
                    &change.id,
                    &change.data,
                    &change.deleted,
                    &device,
                    &change.changed_at,
                ],
            )
            .await
            .map_err(internal_error)?;
    }

    // Images make records large, so rows past the limit are left out here instead of
    // being loaded and thrown away. pg_column_size reads a value's stored size without
    // fetching the value. It's the compressed size, so the loop below has the final say.
    let rows = state
        .db
        .query(
            "SELECT collection, id, data, deleted, seq, page_rows, changed_at FROM (
                SELECT collection, id, data, deleted, seq, changed_at,
                       COUNT(*) OVER () AS page_rows,
                       SUM(pg_column_size(data)) OVER (ORDER BY seq) - pg_column_size(data) AS bytes_before
                FROM (
                    SELECT collection, id, data, deleted, seq, changed_at FROM sync_records
                    WHERE seq > $1 ORDER BY seq LIMIT $2
                ) AS page
             ) AS sized
             WHERE bytes_before < $3 ORDER BY seq",
            &[&request.cursor, &PAGE_SIZE, &(MAX_RESPONSE_BYTES as i64)],
        )
        .await
        .map_err(internal_error)?;

    let page_rows: i64 = rows.first().map_or(0, |row| row.get(5));

    // A slow PC has to finish each page within its time limit or it never gets past
    // it. At least one row always goes out.
    let mut has_more = page_rows == PAGE_SIZE || (rows.len() as i64) < page_rows;
    let mut cursor = request.cursor;
    let mut bytes = 0;
    let mut changes = Vec::new();
    for row in &rows {
        let data: String = row.get(2);
        if !changes.is_empty() && bytes + data.len() > MAX_RESPONSE_BYTES {
            has_more = true;
            break;
        }
        bytes += data.len();
        cursor = row.get(4);
        changes.push(Change {
            collection: row.get(0),
            id: row.get(1),
            data,
            deleted: row.get(3),
            changed_at: row.get(6),
        });
    }

    let epoch = state
        .db
        .query_one("SELECT epoch FROM sync_meta", &[])
        .await
        .map_err(internal_error)?
        .get(0);

    Ok(Json(SyncResponse {
        cursor,
        has_more,
        changes,
        epoch,
    }))
}
