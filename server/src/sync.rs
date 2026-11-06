//! Keeps every device's data in step. The server stores records as opaque JSON
//! text and never reads inside them, so the data's shape can change freely
//! without changes here.

use std::sync::Arc;

use axum::{
    Json,
    extract::State,
    http::{HeaderMap, StatusCode},
};
use serde::{Deserialize, Serialize};
use tokio_postgres::Client;

use crate::{
    AppState,
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
         CREATE INDEX IF NOT EXISTS sync_records_seq ON sync_records (seq);",
    )
    .await
}

#[derive(Serialize, Deserialize)]
pub struct Change {
    collection: String,
    id: String,
    data: String,
    deleted: bool,
}

#[derive(Deserialize)]
pub struct SyncRequest {
    cursor: i64,
    changes: Vec<Change>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SyncResponse {
    cursor: i64,
    has_more: bool,
    changes: Vec<Change>,
}

/// Saves this device's changes, then returns everything newer than its cursor.
///
/// The server has one database connection, so writes run one at a time and
/// sequence numbers become visible in order. A cursor can't skip past a change
/// that's still being written. More connections would need this rethinking.
pub async fn sync(
    State(state): State<Arc<AppState>>,
    headers: HeaderMap,
    Json(request): Json<SyncRequest>,
) -> Result<Json<SyncResponse>, StatusCode> {
    let device = authenticate(&state, &headers).await?;

    if request.changes.len() > MAX_CHANGES_PER_REQUEST {
        return Err(StatusCode::PAYLOAD_TOO_LARGE);
    }

    for change in &request.changes {
        if change.collection.is_empty()
            || change.id.is_empty()
            || change.collection.len() > MAX_KEY_LENGTH
            || change.id.len() > MAX_KEY_LENGTH
        {
            return Err(StatusCode::BAD_REQUEST);
        }

        state
            .db
            .execute(
                "INSERT INTO sync_records (collection, id, data, deleted, seq, updated_by)
                 VALUES ($1, $2, $3, $4, nextval('sync_seq'), $5)
                 ON CONFLICT (collection, id) DO UPDATE SET
                    data = excluded.data,
                    deleted = excluded.deleted,
                    seq = excluded.seq,
                    updated_by = excluded.updated_by",
                &[
                    &change.collection,
                    &change.id,
                    &change.data,
                    &change.deleted,
                    &device,
                ],
            )
            .await
            .map_err(internal_error)?;
    }

    let rows = state
        .db
        .query(
            "SELECT collection, id, data, deleted, seq FROM sync_records
             WHERE seq > $1 ORDER BY seq LIMIT $2",
            &[&request.cursor, &PAGE_SIZE],
        )
        .await
        .map_err(internal_error)?;

    // Images make records large, and a slow PC has to finish each page within its
    // time limit or it never gets past it. At least one row always goes out.
    let mut has_more = rows.len() as i64 == PAGE_SIZE;
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
        });
    }

    Ok(Json(SyncResponse {
        cursor,
        has_more,
        changes,
    }))
}
