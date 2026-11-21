//! Test machines: PCs that let another of the owner's PCs run a project's tests on them, as T3
//! Code's linked environments let one machine's agents work on another. PCs here rarely reach
//! each other, but both always reach this server, so it carries the work as a mailbox.
//!
//! Every PC paired here is the owner's, so linking needs no typed code, as T3 Code mints its own
//! code when it already has a session on the other machine: this server is that session. The PC
//! linking asks for a link to a PC that lets others run tests on it, which is that PC's consent,
//! and that PC answers by itself. The two swap ephemeral P-256 public keys through here, each
//! request and answer coming from a paired login this server checked, and derive the link's key
//! from their ECDH secret, which never comes here. So this server is trusted while a link is
//! made, to pass on the keys as sent, but not after: what it keeps can't sign a job, and a server
//! taken over later can't make one. Jobs carry a MAC made with that key, over a run id and time
//! the target won't take twice or late, and say only which commit of which repo to test: the repo
//! URL is the linking PC's own git remote, and the commands are in the repo, in
//! .agent-orange/tests.json at that commit. So nothing this server or another paired PC can change
//! after linking decides what runs; whoever can push to the repo can, which running its tests
//! already trusts. This server can relay, keep, delay or drop jobs. Verdicts and logs come back
//! unauthenticated. Links are renewed with a request and an answer each MACed with the link's key.
//! Nothing here knows what a project, a tag or a command means.
//!
//! Everything is keyed by the PC (its install), not its login, as a PC has a login at each of
//! this server's addresses.

use std::collections::HashMap;
use std::sync::Arc;
use std::time::{Duration, Instant};

use axum::{
    Json,
    extract::{Path, Query, State},
    http::{HeaderMap, StatusCode},
};
use serde::{Deserialize, Serialize};
use tokio_postgres::{Client, Row};

use crate::{
    AppState,
    devices::{authenticate_install, clean_name, internal_error, random_hex},
    sync::client_is_out_of_date,
};

/// Seen this recently, a PC that accepts runs counts as reachable. PCs sync every 3 seconds.
const REACHABLE_SECONDS: f64 = 15.0;
const MAX_TAGS: usize = 32;
const MAX_TAG_LENGTH: usize = 40;
const MAX_JOB_BYTES: usize = 64 * 1024;
const MAX_LOG_CHUNK_BYTES: usize = 64 * 1024;
const MAX_LOG_BYTES: i64 = 5 * 1024 * 1024;
const MAX_LOG_ROWS_PER_READ: i64 = 400;
const MAX_QUEUED_PER_LINK: i64 = 10;
const MAX_UNANSWERED_PER_LINKER: i64 = 3;
const ACCESS_LEVELS: [&str; 3] = ["tests", "agents", "full"];
/// Checks a run can say how each went, from the results file its repo names.
const MAX_CHECKS: usize = 5000;
const MAX_CHECK_NAME_BYTES: usize = 1024;
const CHECK_OUTCOMES: [&str; 3] = ["passed", "failed", "skipped"];
const MAX_CHECK_HISTORY_ROWS: i64 = 5000;
const MAX_ONE_CHECK_HISTORY_ROWS: i64 = 300;
const RUN_ENDINGS: [&str; 6] = [
    "passed",
    "failed",
    "couldnt_run",
    "timed_out",
    "cancelled",
    "refused",
];

// Link requests a target turned down, counted per PC linking, so one PC can't keep asking. There's
// no limit across all PCs, as one PC could then lock out every other.
const MAX_REJECTED_LINKS: u32 = 5;
const REJECTED_LINK_WINDOW: Duration = Duration::from_secs(60);

#[derive(Default)]
pub struct LinkLimit {
    window_started: Option<Instant>,
    rejected: HashMap<String, u32>,
}

impl LinkLimit {
    fn is_blocked(&mut self, linker: &str) -> bool {
        if self
            .window_started
            .is_some_and(|started| started.elapsed() >= REJECTED_LINK_WINDOW)
        {
            *self = Self::default();
        }
        self.rejected.get(linker).copied().unwrap_or(0) >= MAX_REJECTED_LINKS
    }

    fn record_rejection(&mut self, linker: &str) {
        self.window_started.get_or_insert_with(Instant::now);
        *self.rejected.entry(linker.to_string()).or_insert(0) += 1;
    }
}

pub async fn create_tables(db: &Client) -> Result<(), tokio_postgres::Error> {
    db.batch_execute(
        "CREATE TABLE IF NOT EXISTS test_machines (
            install_hash TEXT PRIMARY KEY,
            label TEXT NOT NULL,
            tags TEXT[] NOT NULL DEFAULT '{}',
            accepting BOOLEAN NOT NULL DEFAULT false,
            updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
         );
         -- Asked for by the PC linking, answered by the target, and gone after 10 minutes.
         CREATE TABLE IF NOT EXISTS machine_link_requests (
            id TEXT PRIMARY KEY,
            machine_install TEXT NOT NULL,
            linker_install TEXT NOT NULL,
            access TEXT NOT NULL,
            nonce TEXT NOT NULL,
            proof TEXT NOT NULL,
            created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
            answer TEXT,
            link_id TEXT
         );
         CREATE INDEX IF NOT EXISTS machine_link_requests_machine ON machine_link_requests (machine_install);
         CREATE TABLE IF NOT EXISTS machine_links (
            id TEXT PRIMARY KEY,
            machine_install TEXT NOT NULL,
            linker_install TEXT NOT NULL,
            access TEXT NOT NULL,
            linked_at TIMESTAMPTZ NOT NULL DEFAULT now(),
            expires_at TIMESTAMPTZ NOT NULL,
            UNIQUE (machine_install, linker_install)
         );
         CREATE TABLE IF NOT EXISTS test_runs (
            id TEXT PRIMARY KEY,
            link_id TEXT NOT NULL,
            machine_install TEXT NOT NULL,
            linker_install TEXT NOT NULL,
            project_id TEXT NOT NULL,
            commit_sha TEXT NOT NULL,
            job TEXT NOT NULL,
            mac TEXT NOT NULL,
            status TEXT NOT NULL,
            exit_code INTEGER,
            cancel_requested BOOLEAN NOT NULL DEFAULT false,
            lease_until TIMESTAMPTZ,
            log_bytes BIGINT NOT NULL DEFAULT 0,
            created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
            started_at TIMESTAMPTZ,
            ended_at TIMESTAMPTZ
         );
         CREATE INDEX IF NOT EXISTS test_runs_machine ON test_runs (machine_install, status, created_at);
         CREATE INDEX IF NOT EXISTS test_runs_linker ON test_runs (linker_install, created_at);
         CREATE TABLE IF NOT EXISTS test_run_logs (
            run_id TEXT NOT NULL REFERENCES test_runs (id) ON DELETE CASCADE,
            seq INTEGER NOT NULL,
            text TEXT NOT NULL,
            PRIMARY KEY (run_id, seq)
         );
         -- How each check in a run went, as its results file said (#105). Only names and outcomes.
         CREATE TABLE IF NOT EXISTS test_run_checks (
            run_id TEXT NOT NULL REFERENCES test_runs (id) ON DELETE CASCADE,
            name TEXT NOT NULL,
            outcome TEXT NOT NULL,
            PRIMARY KEY (run_id, name)
         );
         CREATE INDEX IF NOT EXISTS test_runs_project ON test_runs (project_id, created_at);
         -- Each PC's ephemeral public key, the target's expiry and its confirmation of the key.
         ALTER TABLE machine_link_requests ADD COLUMN IF NOT EXISTS linker_key TEXT NOT NULL DEFAULT '';
         ALTER TABLE machine_link_requests ADD COLUMN IF NOT EXISTS machine_key TEXT;
         ALTER TABLE machine_link_requests ADD COLUMN IF NOT EXISTS machine_expires_at DOUBLE PRECISION;
         ALTER TABLE machine_link_requests ADD COLUMN IF NOT EXISTS confirm TEXT;
         -- The PC linking asks to renew a link, MACed with its key, and the target answers.
         ALTER TABLE machine_links ADD COLUMN IF NOT EXISTS renew_nonce TEXT;
         ALTER TABLE machine_links ADD COLUMN IF NOT EXISTS renew_sent_at BIGINT;
         ALTER TABLE machine_links ADD COLUMN IF NOT EXISTS renew_expires_at BIGINT;
         ALTER TABLE machine_links ADD COLUMN IF NOT EXISTS renew_mac TEXT;
         ALTER TABLE machine_links ADD COLUMN IF NOT EXISTS renewed_nonce TEXT;
         ALTER TABLE machine_links ADD COLUMN IF NOT EXISTS renewed_expires_at BIGINT;
         ALTER TABLE machine_links ADD COLUMN IF NOT EXISTS renewed_mac TEXT;
         -- Why a target turned a request down, in a word the PC linking turns into words.
         ALTER TABLE machine_link_requests ADD COLUMN IF NOT EXISTS reason TEXT;
         -- When the PC last used these routes, which only its own release can, so a PC that syncs
         -- but hasn't been here lately is on another release.
         ALTER TABLE test_machines ADD COLUMN IF NOT EXISTS seen_at TIMESTAMPTZ;
         -- The release this server was on when a run was taken, which its test PC may go on
         -- reporting it from after this server updates, as it doesn't update mid-run (#124).
         ALTER TABLE test_runs ADD COLUMN IF NOT EXISTS started_release TEXT;
         -- When this server last turned away a run's report for coming from another release, so
         -- its window can say why it went quiet, and the report sent after updating is still taken.
         ALTER TABLE test_runs ADD COLUMN IF NOT EXISTS held_back_at TIMESTAMPTZ;
         -- This server's release and the one it ran before.
         CREATE TABLE IF NOT EXISTS server_release (
            id INTEGER PRIMARY KEY CHECK (id = 1),
            current TEXT NOT NULL,
            previous TEXT
         );",
    )
    .await
}

/// Notes this server's release, and gives the one it ran before, whose test PCs may still be
/// finishing runs (#124). None on a first start, or when there's no telling.
pub async fn note_release(
    db: &Client,
    version: &str,
) -> Result<Option<String>, tokio_postgres::Error> {
    let row = db
        .query_one(
            "INSERT INTO server_release (id, current) VALUES (1, $1)
             ON CONFLICT (id) DO UPDATE SET
                previous = CASE WHEN server_release.current = EXCLUDED.current THEN server_release.previous ELSE server_release.current END,
                current = EXCLUDED.current
             RETURNING previous",
            &[&version],
        )
        .await?;
    Ok(row.get(0))
}

/// A run's test PC may go on reporting it from the release the run was taken on, or from this
/// server's release before this one, as a test PC doesn't update while it runs (#124). Dev builds
/// send no version, and never get this far.
fn release_may_report(sent: Option<&str>, started: Option<&str>, previous: Option<&str>) -> bool {
    sent.is_some_and(|sent| started == Some(sent) || previous == Some(sent))
}

/// A run's own routes as its test PC uses them: as `caller`, except that the PC may be on the
/// release the run was taken on (or the one before this server's) until the run ends, so a run
/// that straddles a release still gets its log and its end. Anything else from another release
/// is turned away, and the run notes it, so its window can say its report was held back.
async fn run_caller(state: &AppState, headers: &HeaderMap, id: &str) -> Result<String, StatusCode> {
    let install = authenticate_install(state, headers).await?;
    if !client_is_out_of_date(headers) {
        return seen(state, install).await;
    }
    let started = state
        .db
        .query_opt(
            "SELECT started_release FROM test_runs
             WHERE id = $1 AND machine_install = $2 AND status IN ('running', 'lost')",
            &[&id, &install],
        )
        .await
        .map_err(internal_error)?;
    let Some(started) = started else {
        return Err(StatusCode::UPGRADE_REQUIRED);
    };
    let started: Option<String> = started.get(0);
    let sent = headers
        .get("x-agent-orange-version")
        .and_then(|version| version.to_str().ok());
    // Not noted as seen, as for everything else this PC is still on another release.
    if release_may_report(sent, started.as_deref(), state.previous_release.as_deref()) {
        return Ok(install);
    }
    state
        .db
        .execute(
            "UPDATE test_runs SET held_back_at = now()
             WHERE id = $1 AND machine_install = $2 AND status IN ('running', 'lost')",
            &[&id, &install],
        )
        .await
        .map_err(internal_error)?;
    Err(StatusCode::UPGRADE_REQUIRED)
}

/// Every route here needs a login from a PC that said which PC it is, and this server's own
/// release, as test machines are new and nothing about them stays the same across releases.
async fn caller(state: &AppState, headers: &HeaderMap) -> Result<String, StatusCode> {
    let install = authenticate_install(state, headers).await?;
    if client_is_out_of_date(headers) {
        return Err(StatusCode::UPGRADE_REQUIRED);
    }
    seen(state, install).await
}

// Only this server's own release counts as seen here.
async fn seen(state: &AppState, install: String) -> Result<String, StatusCode> {
    state
        .db
        .execute(
            "UPDATE test_machines SET seen_at = now() WHERE install_hash = $1",
            &[&install],
        )
        .await
        .map_err(internal_error)?;
    Ok(install)
}

/// Links to or from a PC whose every login was removed can't be used by anyone any more.
pub async fn forget_removed_pcs(db: &Client) -> Result<(), tokio_postgres::Error> {
    db.batch_execute(
        "DELETE FROM machine_links l WHERE
            NOT EXISTS (SELECT 1 FROM devices d WHERE d.install_hash = l.machine_install)
            OR NOT EXISTS (SELECT 1 FROM devices d WHERE d.install_hash = l.linker_install);
         DELETE FROM test_machines m WHERE
            NOT EXISTS (SELECT 1 FROM devices d WHERE d.install_hash = m.install_hash);
         UPDATE test_runs SET status = 'cancelled', ended_at = now()
            WHERE status = 'queued' AND NOT EXISTS (SELECT 1 FROM machine_links l WHERE l.id = test_runs.link_id);",
    )
    .await
}

/// A run whose machine stopped renewing its lease is lost, and one that waited a day without
/// being started has expired. Settled whenever runs are looked at.
async fn settle_lost_runs(db: &Client) -> Result<(), StatusCode> {
    db.batch_execute(
        "UPDATE test_runs SET status = 'lost', ended_at = now()
            WHERE status = 'running' AND lease_until < now();
         UPDATE test_runs SET status = 'expired', ended_at = now()
            WHERE status = 'queued' AND created_at < now() - interval '24 hours';",
    )
    .await
    .map_err(internal_error)?;
    Ok(())
}

/// A PC that synced lately but hasn't used these routes for this long is on another release.
const OTHER_RELEASE_SECONDS: f64 = 60.0;

/// The PC's state: `machine_seen` is when it last used these routes, `pc_seen` when it last used
/// any, such as to sync.
fn status(
    expired: bool,
    accepting: bool,
    machine_seen: f64,
    pc_seen: f64,
    now: f64,
    busy: bool,
) -> &'static str {
    if expired {
        "expired"
    } else if now - pc_seen <= REACHABLE_SECONDS && now - machine_seen > OTHER_RELEASE_SECONDS {
        "other_release"
    } else if !accepting || now - machine_seen > REACHABLE_SECONDS {
        "unreachable"
    } else if busy {
        "busy"
    } else {
        "reachable"
    }
}

// Machines

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct MachineView {
    install: String,
    label: String,
    tags: Vec<String>,
    accepting: bool,
    status: &'static str,
    last_seen_at: f64,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LinkView {
    id: String,
    machine: String,
    machine_label: String,
    machine_tags: Vec<String>,
    linker: String,
    linker_label: String,
    access: String,
    linked_at: f64,
    expires_at: f64,
    status: &'static str,
    last_seen_at: f64,
    /// A renewal the PC linking asked for, which the target checks and answers.
    renewal: Option<Renewal>,
    /// The target's answer to the last renewal, which the PC linking checks.
    renewed: Option<Renewed>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Renewal {
    nonce: String,
    sent_at: i64,
    expires_at: i64,
    mac: String,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Renewed {
    nonce: String,
    expires_at: i64,
    mac: String,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct MachinesResponse {
    /// This PC, as the others know it, which the proof of a link names.
    me: String,
    /// What this PC said about itself, if it ever did.
    this_machine: Option<MachineView>,
    /// The other PCs that let others run tests on them.
    machines: Vec<MachineView>,
    /// Links from this PC to others, and from others to this one.
    links: Vec<LinkView>,
    /// The owner's other PCs paired here that have never said whether they take runs, so the PC
    /// linking can say why they can't be linked yet.
    others: Vec<OtherPc>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OtherPc {
    label: String,
    /// "other_release" when it's on a release from before test machines or another one, or
    /// "pair_again" when it paired before pairing said which PC it was.
    reason: &'static str,
    last_seen_at: f64,
}

const LAST_SEEN: &str = "COALESCE((SELECT EXTRACT(EPOCH FROM MAX(d.last_seen_at))::float8 FROM devices d WHERE d.install_hash = {pc}), 0)";
const MACHINE_SEEN: &str = "COALESCE(EXTRACT(EPOCH FROM m.seen_at)::float8, 0)";
const BUSY: &str = "EXISTS (SELECT 1 FROM test_runs r WHERE r.machine_install = {pc} AND r.status = 'running' AND r.lease_until > now())";

pub async fn list(
    State(state): State<Arc<AppState>>,
    headers: HeaderMap,
) -> Result<Json<MachinesResponse>, StatusCode> {
    let me = caller(&state, &headers).await?;
    settle_lost_runs(&state.db).await?;

    let machine_rows = state
        .db
        .query(
            &format!(
                "SELECT m.install_hash, m.label, m.tags, m.accepting, {MACHINE_SEEN}, {}, EXTRACT(EPOCH FROM now())::float8, {}
                 FROM test_machines m
                 WHERE EXISTS (SELECT 1 FROM devices d WHERE d.install_hash = m.install_hash)
                 ORDER BY lower(m.label)",
                BUSY.replace("{pc}", "m.install_hash"),
                LAST_SEEN.replace("{pc}", "m.install_hash"),
            ),
            &[],
        )
        .await
        .map_err(internal_error)?;

    let mut this_machine = None;
    let mut machines = Vec::new();
    for row in &machine_rows {
        let view = MachineView {
            install: row.get(0),
            label: row.get(1),
            tags: row.get(2),
            accepting: row.get(3),
            last_seen_at: row.get(4),
            status: status(
                false,
                row.get(3),
                row.get(4),
                row.get(7),
                row.get(6),
                row.get(5),
            ),
        };
        if view.install == me {
            this_machine = Some(view);
        } else {
            machines.push(view);
        }
    }

    let link_rows = state
        .db
        .query(
            &format!(
                "SELECT l.id, l.machine_install, l.linker_install, l.access,
                    EXTRACT(EPOCH FROM l.linked_at)::float8, EXTRACT(EPOCH FROM l.expires_at)::float8,
                    l.expires_at <= now(),
                    COALESCE(m.label, (SELECT d.name FROM devices d WHERE d.install_hash = l.machine_install LIMIT 1), ''),
                    COALESCE((SELECT d.name FROM devices d WHERE d.install_hash = l.linker_install LIMIT 1), ''),
                    COALESCE(m.accepting, false), COALESCE(m.tags, '{{}}'),
                    {}, {}, EXTRACT(EPOCH FROM now())::float8,
                    l.renew_nonce, l.renew_sent_at, l.renew_expires_at, l.renew_mac,
                    l.renewed_nonce, l.renewed_expires_at, l.renewed_mac, {}
                 FROM machine_links l LEFT JOIN test_machines m ON m.install_hash = l.machine_install
                 WHERE l.machine_install = $1 OR l.linker_install = $1
                 ORDER BY l.linked_at",
                MACHINE_SEEN,
                BUSY.replace("{pc}", "l.machine_install"),
                LAST_SEEN.replace("{pc}", "l.machine_install"),
            ),
            &[&me],
        )
        .await
        .map_err(internal_error)?;

    let links = link_rows
        .iter()
        .map(|row| LinkView {
            id: row.get(0),
            machine: row.get(1),
            linker: row.get(2),
            access: row.get(3),
            linked_at: row.get(4),
            expires_at: row.get(5),
            machine_label: row.get(7),
            linker_label: row.get(8),
            machine_tags: row.get(10),
            last_seen_at: row.get(11),
            renewal: match (row.get(14), row.get(15), row.get(16), row.get(17)) {
                (Some(nonce), Some(sent_at), Some(expires_at), Some(mac)) => Some(Renewal {
                    nonce,
                    sent_at,
                    expires_at,
                    mac,
                }),
                _ => None,
            },
            renewed: match (row.get(18), row.get(19), row.get(20)) {
                (Some(nonce), Some(expires_at), Some(mac)) => Some(Renewed {
                    nonce,
                    expires_at,
                    mac,
                }),
                _ => None,
            },
            status: status(
                row.get(6),
                row.get(9),
                row.get(11),
                row.get(21),
                row.get(13),
                row.get(12),
            ),
        })
        .collect();

    // PCs, by install, that never said anything here; a login without an install is a PC of its own.
    let other_rows = state
        .db
        .query(
            "SELECT MIN(d.name), bool_or(d.install_hash IS NULL), EXTRACT(EPOCH FROM MAX(d.last_seen_at))::float8
             FROM devices d
             WHERE d.install_hash IS NULL
                OR (d.install_hash <> $1 AND NOT EXISTS (SELECT 1 FROM test_machines m WHERE m.install_hash = d.install_hash))
             GROUP BY COALESCE(d.install_hash, d.id)
             ORDER BY lower(MIN(d.name))",
            &[&me],
        )
        .await
        .map_err(internal_error)?;
    let others = other_rows
        .iter()
        .map(|row| OtherPc {
            label: row.get(0),
            reason: if row.get(1) {
                "pair_again"
            } else {
                "other_release"
            },
            last_seen_at: row.get(2),
        })
        .collect();

    Ok(Json(MachinesResponse {
        me,
        this_machine,
        machines,
        links,
        others,
    }))
}

#[derive(Deserialize)]
pub struct PublishRequest {
    label: String,
    #[serde(default)]
    tags: Vec<String>,
    accepting: bool,
}

/// What this PC offers: a name, free-form tags, and whether it takes runs now.
pub async fn publish(
    State(state): State<Arc<AppState>>,
    headers: HeaderMap,
    Json(request): Json<PublishRequest>,
) -> Result<StatusCode, StatusCode> {
    let me = caller(&state, &headers).await?;
    let label = clean_name(&request.label)?;
    let mut tags: Vec<String> = Vec::new();
    for tag in &request.tags {
        let tag = tag.trim();
        if tag.is_empty()
            || tag.chars().count() > MAX_TAG_LENGTH
            || tag.chars().any(char::is_control)
        {
            return Err(StatusCode::BAD_REQUEST);
        }
        if !tags.iter().any(|known| known.eq_ignore_ascii_case(tag)) {
            tags.push(tag.to_string());
        }
    }
    if tags.len() > MAX_TAGS {
        return Err(StatusCode::BAD_REQUEST);
    }

    state
        .db
        .execute(
            "INSERT INTO test_machines (install_hash, label, tags, accepting) VALUES ($1, $2, $3, $4)
             ON CONFLICT (install_hash) DO UPDATE SET
                label = excluded.label, tags = excluded.tags, accepting = excluded.accepting, updated_at = now()",
            &[&me, &label, &tags, &request.accepting],
        )
        .await
        .map_err(internal_error)?;

    // No longer taking runs, so requests still waiting for it won't be answered: said now, so the
    // PC linking needn't wait them out.
    if !request.accepting {
        state
            .db
            .execute(
                "UPDATE machine_link_requests SET answer = 'rejected', reason = 'not_accepting'
                 WHERE machine_install = $1 AND answer IS NULL",
                &[&me],
            )
            .await
            .map_err(internal_error)?;
    }
    Ok(StatusCode::NO_CONTENT)
}

// Linking

fn is_hex(text: &str, length: usize) -> bool {
    text.len() == length && text.bytes().all(|byte| byte.is_ascii_hexdigit())
}

// A public key as base64 of its DER, which for P-256 is 124 characters. Opaque here.
fn is_public_key(text: &str) -> bool {
    (60..=256).contains(&text.len())
        && text.bytes().all(|byte| {
            byte.is_ascii_alphanumeric() || byte == b'+' || byte == b'/' || byte == b'='
        })
}

#[derive(Deserialize)]
pub struct LinkRequestBody {
    access: String,
    nonce: String,
    /// The PC linking's ephemeral public key, which the link's key is derived from.
    key: String,
}

#[derive(Serialize)]
pub struct Created {
    id: String,
}

/// Asks a PC for a link, which it answers by itself while it lets others run tests on it. This
/// login being one of the owner's PCs is what's checked, as pairing with this server already did.
pub async fn request_link(
    State(state): State<Arc<AppState>>,
    headers: HeaderMap,
    Path(machine): Path<String>,
    Json(body): Json<LinkRequestBody>,
) -> Result<Json<Created>, StatusCode> {
    let me = caller(&state, &headers).await?;
    if !ACCESS_LEVELS.contains(&body.access.as_str())
        || !is_hex(&body.nonce, 32)
        || !is_public_key(&body.key)
    {
        return Err(StatusCode::BAD_REQUEST);
    }
    if machine == me {
        return Err(StatusCode::UNPROCESSABLE_ENTITY);
    }
    if state.link_limit.lock().unwrap().is_blocked(&me) {
        return Err(StatusCode::TOO_MANY_REQUESTS);
    }

    state
        .db
        .execute(
            "DELETE FROM machine_link_requests WHERE created_at < now() - interval '10 minutes'",
            &[],
        )
        .await
        .map_err(internal_error)?;

    // Only a PC that lets others run tests on it can be linked to.
    let accepting = state
        .db
        .query_opt(
            "SELECT accepting FROM test_machines m WHERE install_hash = $1
                AND EXISTS (SELECT 1 FROM devices d WHERE d.install_hash = m.install_hash)",
            &[&machine],
        )
        .await
        .map_err(internal_error)?
        .map(|row| row.get::<_, bool>(0));
    match accepting {
        None => return Err(StatusCode::NOT_FOUND),
        Some(false) => return Err(StatusCode::CONFLICT),
        Some(true) => {}
    }

    // A few at a time, so a PC can't queue up requests faster than the target answers them.
    let id = random_hex::<16>();
    let inserted = state
        .db
        .execute(
            "INSERT INTO machine_link_requests (id, machine_install, linker_install, access, nonce, proof, linker_key)
             SELECT $1, $2, $3, $4, $5, '', $7
             WHERE (SELECT count(*) FROM machine_link_requests WHERE linker_install = $3 AND answer IS NULL) < $6",
            &[
                &id,
                &machine,
                &me,
                &body.access,
                &body.nonce,
                &MAX_UNANSWERED_PER_LINKER,
                &body.key,
            ],
        )
        .await
        .map_err(internal_error)?;
    if inserted == 0 {
        return Err(StatusCode::TOO_MANY_REQUESTS);
    }
    Ok(Json(Created { id }))
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PendingLinkRequest {
    id: String,
    linker: String,
    linker_label: String,
    access: String,
    nonce: String,
    key: String,
}

/// Requests waiting on this PC's answer.
pub async fn pending_link_requests(
    State(state): State<Arc<AppState>>,
    headers: HeaderMap,
) -> Result<Json<Vec<PendingLinkRequest>>, StatusCode> {
    let me = caller(&state, &headers).await?;
    let rows = state
        .db
        .query(
            "SELECT r.id, r.linker_install, COALESCE((SELECT d.name FROM devices d WHERE d.install_hash = r.linker_install LIMIT 1), ''),
                r.access, r.nonce, r.linker_key
             FROM machine_link_requests r
             WHERE r.machine_install = $1 AND r.answer IS NULL AND r.created_at > now() - interval '10 minutes'
             ORDER BY r.created_at",
            &[&me],
        )
        .await
        .map_err(internal_error)?;
    Ok(Json(
        rows.iter()
            .map(|row| PendingLinkRequest {
                id: row.get(0),
                linker: row.get(1),
                linker_label: row.get(2),
                access: row.get(3),
                nonce: row.get(4),
                key: row.get(5),
            })
            .collect(),
    ))
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LinkAnswer {
    answer: Option<String>,
    link_id: Option<String>,
    /// As the target set it, which its confirmation covers.
    expires_at: Option<f64>,
    key: Option<String>,
    confirm: Option<String>,
    /// Why the target turned it down, when it did.
    reason: Option<String>,
}

/// The PC linking waits on this for the target's answer.
pub async fn link_request(
    State(state): State<Arc<AppState>>,
    headers: HeaderMap,
    Path(id): Path<String>,
) -> Result<Json<LinkAnswer>, StatusCode> {
    let me = caller(&state, &headers).await?;
    let row = state
        .db
        .query_opt(
            "SELECT r.answer, r.link_id, r.machine_expires_at, r.machine_key, r.confirm, r.reason
             FROM machine_link_requests r
             WHERE r.id = $1 AND r.linker_install = $2 AND r.created_at > now() - interval '10 minutes'",
            &[&id, &me],
        )
        .await
        .map_err(internal_error)?
        .ok_or(StatusCode::NOT_FOUND)?;
    Ok(Json(LinkAnswer {
        answer: row.get(0),
        link_id: row.get(1),
        expires_at: row.get(2),
        key: row.get(3),
        confirm: row.get(4),
        reason: row.get(5),
    }))
}

/// The PC linking gave up waiting, so the target won't answer it. Not one already answered,
/// which the PC linking reads instead, to end a link it won't keep.
pub async fn withdraw_link_request(
    State(state): State<Arc<AppState>>,
    headers: HeaderMap,
    Path(id): Path<String>,
) -> Result<StatusCode, StatusCode> {
    let me = caller(&state, &headers).await?;
    let removed = state
        .db
        .execute(
            "DELETE FROM machine_link_requests WHERE id = $1 AND linker_install = $2 AND answer IS NULL",
            &[&id, &me],
        )
        .await
        .map_err(internal_error)?;
    if removed == 0 {
        return Err(StatusCode::NOT_FOUND);
    }
    Ok(StatusCode::NO_CONTENT)
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AcceptBody {
    /// Made by the target, and part of what the link's key is derived from.
    id: String,
    /// The target's ephemeral public key.
    key: String,
    /// When the target says the link ends, which it decided itself.
    expires_at: f64,
    /// The target's MAC over the whole exchange, keyed with the link's key, which the PC linking
    /// checks before it keeps the link, so both are sure they have the same key.
    confirm: String,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Accepted {
    expires_at: f64,
}

/// The target took the request. A link between the same two PCs is replaced, which is how one
/// that expired is made again.
pub async fn accept_link_request(
    State(state): State<Arc<AppState>>,
    headers: HeaderMap,
    Path(request): Path<String>,
    Json(body): Json<AcceptBody>,
) -> Result<Json<Accepted>, StatusCode> {
    let me = caller(&state, &headers).await?;
    if !is_hex(&body.id, 32)
        || !is_public_key(&body.key)
        || !is_hex(&body.confirm, 64)
        || !body.expires_at.is_finite()
    {
        return Err(StatusCode::BAD_REQUEST);
    }
    let row = state
        .db
        .query_opt(
            "WITH answered AS (
                UPDATE machine_link_requests SET answer = 'accepted', link_id = $3,
                    machine_key = $4, confirm = $5, machine_expires_at = $6
                WHERE id = $1 AND machine_install = $2 AND answer IS NULL AND created_at > now() - interval '10 minutes'
                RETURNING machine_install, linker_install, access
             )
             INSERT INTO machine_links (id, machine_install, linker_install, access, expires_at)
             SELECT $3, machine_install, linker_install, access, now() + interval '30 days' FROM answered
             ON CONFLICT (machine_install, linker_install) DO UPDATE SET
                id = excluded.id, access = excluded.access, linked_at = now(), expires_at = excluded.expires_at
             RETURNING EXTRACT(EPOCH FROM expires_at)::float8",
            &[
                &request,
                &me,
                &body.id,
                &body.key,
                &body.confirm,
                &body.expires_at,
            ],
        )
        .await
        .map_err(internal_error)?
        .ok_or(StatusCode::NOT_FOUND)?;

    // Queued under the link this replaced, and signed with its key, which neither PC has now.
    forget_removed_pcs(&state.db)
        .await
        .map_err(internal_error)?;
    Ok(Json(Accepted {
        expires_at: row.get(0),
    }))
}

/// Why a target turns a request down, which the PC linking says in words.
const REJECT_REASONS: [&str; 3] = ["not_accepting", "access", "unreadable"];

#[derive(Deserialize)]
pub struct RejectBody {
    reason: String,
}

/// The target won't take the request: it no longer lets others run tests on it, the access
/// asked for is more than it offers, or the key sent isn't one. Counts against the PC that asked.
pub async fn reject_link_request(
    State(state): State<Arc<AppState>>,
    headers: HeaderMap,
    Path(request): Path<String>,
    Json(body): Json<RejectBody>,
) -> Result<StatusCode, StatusCode> {
    let me = caller(&state, &headers).await?;
    if !REJECT_REASONS.contains(&body.reason.as_str()) {
        return Err(StatusCode::BAD_REQUEST);
    }
    let row = state
        .db
        .query_opt(
            "UPDATE machine_link_requests SET answer = 'rejected', reason = $3
             WHERE id = $1 AND machine_install = $2 AND answer IS NULL
             RETURNING linker_install",
            &[&request, &me, &body.reason],
        )
        .await
        .map_err(internal_error)?
        .ok_or(StatusCode::NOT_FOUND)?;
    state
        .link_limit
        .lock()
        .unwrap()
        .record_rejection(&row.get::<_, String>(0));
    Ok(StatusCode::NO_CONTENT)
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RenewBody {
    nonce: String,
    sent_at: i64,
    expires_at: i64,
    /// Made with the link's key, which only the two PCs have. Opaque here.
    mac: String,
}

/// The PC linking asks to renew a link that's still good. Only the target can check the request
/// and answer it, so this only keeps it for the target to see in its list.
pub async fn request_renewal(
    State(state): State<Arc<AppState>>,
    headers: HeaderMap,
    Path(id): Path<String>,
    Json(body): Json<RenewBody>,
) -> Result<StatusCode, StatusCode> {
    let me = caller(&state, &headers).await?;
    if !is_hex(&body.nonce, 32) || !is_hex(&body.mac, 64) {
        return Err(StatusCode::BAD_REQUEST);
    }
    let asked = state
        .db
        .execute(
            "UPDATE machine_links SET renew_nonce = $3, renew_sent_at = $4, renew_expires_at = $5, renew_mac = $6
             WHERE id = $1 AND linker_install = $2 AND expires_at > now()",
            &[&id, &me, &body.nonce, &body.sent_at, &body.expires_at, &body.mac],
        )
        .await
        .map_err(internal_error)?;
    if asked == 0 {
        // Gone, expired, or not this PC's: only linking again links them.
        return Err(StatusCode::NOT_FOUND);
    }
    Ok(StatusCode::NO_CONTENT)
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RenewedBody {
    nonce: String,
    expires_at: i64,
    mac: String,
}

/// The target answers the renewal it was asked for, with the expiry it set itself. This server's
/// own expiry for the link moves on as far, and never further than 30 days from now.
pub async fn answer_renewal(
    State(state): State<Arc<AppState>>,
    headers: HeaderMap,
    Path(id): Path<String>,
    Json(body): Json<RenewedBody>,
) -> Result<StatusCode, StatusCode> {
    let me = caller(&state, &headers).await?;
    if !is_hex(&body.nonce, 32) || !is_hex(&body.mac, 64) {
        return Err(StatusCode::BAD_REQUEST);
    }
    let answered = state
        .db
        .execute(
            "UPDATE machine_links SET renewed_nonce = $3, renewed_expires_at = $4, renewed_mac = $5,
                expires_at = LEAST(to_timestamp($4::bigint), now() + interval '30 days')
             WHERE id = $1 AND machine_install = $2 AND renew_nonce = $3 AND expires_at > now()",
            &[&id, &me, &body.nonce, &body.expires_at, &body.mac],
        )
        .await
        .map_err(internal_error)?;
    if answered == 0 {
        return Err(StatusCode::NOT_FOUND);
    }
    Ok(StatusCode::NO_CONTENT)
}

/// Either PC can end a link, and that's final. Its queued runs are cancelled, and a running one
/// is told to stop.
pub async fn remove_link(
    State(state): State<Arc<AppState>>,
    headers: HeaderMap,
    Path(id): Path<String>,
) -> Result<StatusCode, StatusCode> {
    let me = caller(&state, &headers).await?;
    let removed = state
        .db
        .execute(
            "DELETE FROM machine_links WHERE id = $1 AND (machine_install = $2 OR linker_install = $2)",
            &[&id, &me],
        )
        .await
        .map_err(internal_error)?;
    if removed == 0 {
        return Err(StatusCode::NOT_FOUND);
    }
    state
        .db
        .execute(
            "UPDATE test_runs SET
                status = CASE WHEN status = 'queued' THEN 'cancelled' ELSE status END,
                ended_at = CASE WHEN status = 'queued' THEN now() ELSE ended_at END,
                cancel_requested = true
             WHERE link_id = $1 AND status IN ('queued', 'running')",
            &[&id],
        )
        .await
        .map_err(internal_error)?;
    Ok(StatusCode::NO_CONTENT)
}

// Runs

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RunView {
    id: String,
    link_id: String,
    machine: String,
    machine_label: String,
    linker: String,
    linker_label: String,
    project_id: String,
    commit: String,
    job: String,
    mac: String,
    status: String,
    exit_code: Option<i32>,
    cancel_requested: bool,
    created_at: f64,
    started_at: Option<f64>,
    ended_at: Option<f64>,
    /// The link it was sent under, as it is now: its access, and when it ends. None once it's gone.
    link_access: Option<String>,
    link_expires_at: Option<f64>,
    /// How many of its checks passed, failed or were skipped, when its repo names a results file.
    checks_passed: i64,
    checks_failed: i64,
    checks_skipped: i64,
    /// Its report was turned away for coming from another release, and its test PC sends it once
    /// it has updated (#124).
    held_back: bool,
}

const RUN_COLUMNS: &str = "r.id, r.link_id, r.machine_install,
    COALESCE((SELECT m.label FROM test_machines m WHERE m.install_hash = r.machine_install),
             (SELECT d.name FROM devices d WHERE d.install_hash = r.machine_install LIMIT 1), ''),
    r.linker_install,
    COALESCE((SELECT d.name FROM devices d WHERE d.install_hash = r.linker_install LIMIT 1), ''),
    r.project_id, r.commit_sha, r.job, r.mac, r.status, r.exit_code, r.cancel_requested,
    EXTRACT(EPOCH FROM r.created_at)::float8, EXTRACT(EPOCH FROM r.started_at)::float8, EXTRACT(EPOCH FROM r.ended_at)::float8,
    (SELECT l.access FROM machine_links l WHERE l.id = r.link_id),
    (SELECT EXTRACT(EPOCH FROM l.expires_at)::float8 FROM machine_links l WHERE l.id = r.link_id),
    (SELECT count(*) FROM test_run_checks c WHERE c.run_id = r.id AND c.outcome = 'passed'),
    (SELECT count(*) FROM test_run_checks c WHERE c.run_id = r.id AND c.outcome = 'failed'),
    (SELECT count(*) FROM test_run_checks c WHERE c.run_id = r.id AND c.outcome = 'skipped'),
    r.held_back_at IS NOT NULL";

// A machine's name as runs show it: the one it publishes, or else its PC's.
const MACHINE_LABEL: &str =
    "COALESCE((SELECT m.label FROM test_machines m WHERE m.install_hash = r.machine_install),
             (SELECT d.name FROM devices d WHERE d.install_hash = r.machine_install LIMIT 1), '')";

fn run_view(row: &Row) -> RunView {
    RunView {
        id: row.get(0),
        link_id: row.get(1),
        machine: row.get(2),
        machine_label: row.get(3),
        linker: row.get(4),
        linker_label: row.get(5),
        project_id: row.get(6),
        commit: row.get(7),
        job: row.get(8),
        mac: row.get(9),
        status: row.get(10),
        exit_code: row.get(11),
        cancel_requested: row.get(12),
        created_at: row.get(13),
        started_at: row.get(14),
        ended_at: row.get(15),
        link_access: row.get(16),
        link_expires_at: row.get(17),
        checks_passed: row.get(18),
        checks_failed: row.get(19),
        checks_skipped: row.get(20),
        held_back: row.get(21),
    }
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct EnqueueBody {
    /// Made by the PC linking and covered by the MAC, so the target can refuse one it's run.
    id: String,
    link_id: String,
    project_id: String,
    commit: String,
    /// Opaque here: the PC linking writes it, and the target checks its MAC before running it.
    job: String,
    mac: String,
}

/// Sends a project's tests to a PC this one is linked to.
pub async fn enqueue(
    State(state): State<Arc<AppState>>,
    headers: HeaderMap,
    Json(body): Json<EnqueueBody>,
) -> Result<Json<Created>, StatusCode> {
    let me = caller(&state, &headers).await?;
    let commit_ok = (7..=64).contains(&body.commit.len())
        && body.commit.bytes().all(|byte| byte.is_ascii_hexdigit());
    if !commit_ok
        || !is_hex(&body.id, 32)
        || !is_hex(&body.mac, 64)
        || body.job.len() > MAX_JOB_BYTES
        || body.project_id.is_empty()
        || body.project_id.len() > 128
        || [&body.job, &body.project_id]
            .iter()
            .any(|text| text.contains('\0'))
    {
        return Err(StatusCode::BAD_REQUEST);
    }

    // Kept a month, which is plenty to look back on.
    state
        .db
        .execute(
            "DELETE FROM test_runs WHERE ended_at < now() - interval '30 days'",
            &[],
        )
        .await
        .map_err(internal_error)?;

    let id = body.id.to_ascii_lowercase();
    let inserted = state
        .db
        .execute(
            "INSERT INTO test_runs (id, link_id, machine_install, linker_install, project_id, commit_sha, job, mac, status)
             SELECT $1, l.id, l.machine_install, l.linker_install, $3, $4, $5, $6, 'queued'
             FROM machine_links l
             WHERE l.id = $2 AND l.linker_install = $7 AND l.expires_at > now()
                AND (SELECT count(*) FROM test_runs q WHERE q.link_id = l.id AND q.status = 'queued') < $8
             ON CONFLICT (id) DO NOTHING",
            &[
                &id,
                &body.link_id,
                &body.project_id,
                &body.commit.to_ascii_lowercase(),
                &body.job,
                &body.mac,
                &me,
                &MAX_QUEUED_PER_LINK,
            ],
        )
        .await
        .map_err(internal_error)?;
    if inserted == 0 {
        // Gone, expired, not this PC's, too many waiting, or that id is taken.
        return Err(StatusCode::CONFLICT);
    }
    Ok(Json(Created { id }))
}

#[derive(Deserialize)]
pub struct RunsQuery {
    project: Option<String>,
    /// How many, newest first: 30 unless asked, and at most 200.
    limit: Option<i64>,
}

const DEFAULT_RUNS_LISTED: i64 = 30;
const MOST_RUNS_LISTED: i64 = 200;

/// How many runs a list gives, from what was asked.
fn runs_listed(asked: Option<i64>) -> i64 {
    asked
        .unwrap_or(DEFAULT_RUNS_LISTED)
        .clamp(1, MOST_RUNS_LISTED)
}

/// The latest runs this PC sent or ran. The project page asks for more, to find the last verdict
/// on its default branch.
pub async fn list_runs(
    State(state): State<Arc<AppState>>,
    headers: HeaderMap,
    Query(query): Query<RunsQuery>,
) -> Result<Json<Vec<RunView>>, StatusCode> {
    let me = caller(&state, &headers).await?;
    settle_lost_runs(&state.db).await?;
    let rows = state
        .db
        .query(
            &format!(
                "SELECT {RUN_COLUMNS} FROM test_runs r
                 WHERE (r.linker_install = $1 OR r.machine_install = $1) AND ($2::text IS NULL OR r.project_id = $2)
                 ORDER BY r.created_at DESC LIMIT $3"
            ),
            &[&me, &query.project, &runs_listed(query.limit)],
        )
        .await
        .map_err(internal_error)?;
    Ok(Json(rows.iter().map(run_view).collect()))
}

#[derive(Serialize)]
pub struct LogLine {
    seq: i32,
    text: String,
}

#[derive(Serialize)]
pub struct RunWithLog {
    #[serde(flatten)]
    run: RunView,
    log: Vec<LogLine>,
}

#[derive(Deserialize)]
pub struct ReadQuery {
    #[serde(default)]
    after: Option<i32>,
}

/// A run and its log after the part already read, for either PC.
pub async fn read_run(
    State(state): State<Arc<AppState>>,
    headers: HeaderMap,
    Path(id): Path<String>,
    Query(query): Query<ReadQuery>,
) -> Result<Json<RunWithLog>, StatusCode> {
    let me = caller(&state, &headers).await?;
    settle_lost_runs(&state.db).await?;
    let row = state
        .db
        .query_opt(
            &format!(
                "SELECT {RUN_COLUMNS} FROM test_runs r
                 WHERE r.id = $1 AND (r.linker_install = $2 OR r.machine_install = $2)"
            ),
            &[&id, &me],
        )
        .await
        .map_err(internal_error)?
        .ok_or(StatusCode::NOT_FOUND)?;
    let log = state
        .db
        .query(
            "SELECT seq, text FROM test_run_logs WHERE run_id = $1 AND seq > $2 ORDER BY seq LIMIT $3",
            &[&id, &query.after.unwrap_or(-1), &MAX_LOG_ROWS_PER_READ],
        )
        .await
        .map_err(internal_error)?
        .iter()
        .map(|row| LogLine {
            seq: row.get(0),
            text: row.get(1),
        })
        .collect();
    Ok(Json(RunWithLog {
        run: run_view(&row),
        log,
    }))
}

/// Either PC can cancel. One waiting is cancelled at once; one running is told to stop.
pub async fn cancel_run(
    State(state): State<Arc<AppState>>,
    headers: HeaderMap,
    Path(id): Path<String>,
) -> Result<StatusCode, StatusCode> {
    let me = caller(&state, &headers).await?;
    let changed = state
        .db
        .execute(
            "UPDATE test_runs SET
                status = CASE WHEN status = 'queued' THEN 'cancelled' ELSE status END,
                ended_at = CASE WHEN status = 'queued' THEN now() ELSE ended_at END,
                cancel_requested = true
             WHERE id = $1 AND (linker_install = $2 OR machine_install = $2) AND status IN ('queued', 'running')",
            &[&id, &me],
        )
        .await
        .map_err(internal_error)?;
    if changed == 0 {
        return Err(StatusCode::CONFLICT);
    }
    Ok(StatusCode::NO_CONTENT)
}

/// The target takes the oldest run waiting for it, one at a time, under a lease it renews.
pub async fn claim_next(
    State(state): State<Arc<AppState>>,
    headers: HeaderMap,
) -> Result<axum::response::Response, StatusCode> {
    use axum::response::IntoResponse;
    let me = caller(&state, &headers).await?;
    settle_lost_runs(&state.db).await?;
    let claimed = state
        .db
        .query_opt(
            "UPDATE test_runs SET status = 'running', started_at = now(), lease_until = now() + interval '2 minutes',
                started_release = $2
             WHERE id = (
                SELECT q.id FROM test_runs q JOIN machine_links l ON l.id = q.link_id
                WHERE q.machine_install = $1 AND q.status = 'queued' AND l.expires_at > now()
                    AND NOT EXISTS (SELECT 1 FROM test_runs b WHERE b.machine_install = $1 AND b.status = 'running')
                ORDER BY q.created_at LIMIT 1
                FOR UPDATE OF q SKIP LOCKED
             )
             RETURNING id",
            &[&me, &crate::VERSION],
        )
        .await
        .map_err(internal_error)?;
    let Some(claimed) = claimed else {
        return Ok(StatusCode::NO_CONTENT.into_response());
    };
    let id: String = claimed.get(0);
    let row = state
        .db
        .query_one(
            &format!("SELECT {RUN_COLUMNS} FROM test_runs r WHERE r.id = $1"),
            &[&id],
        )
        .await
        .map_err(internal_error)?;
    Ok(Json(run_view(&row)).into_response())
}

#[derive(Serialize)]
pub struct KeepGoing {
    /// Someone cancelled it, or its link ended, so the target should stop it.
    cancel: bool,
    /// The log is full, so the rest isn't kept.
    truncated: bool,
}

/// How long after a run was called lost its test PC may still send the report it held back.
const HELD_BACK_KEPT: &str = "interval '24 hours'";

// A run of this PC's that it's still running, and whether it should stop. Held back, a run lost
// meanwhile counts too, as its test PC sends what it kept once it has updated (#124).
async fn running_run(
    db: &Client,
    id: &str,
    me: &str,
    held_back: bool,
) -> Result<(bool, i64), StatusCode> {
    let row = db
        .query_opt(
            &format!(
                "SELECT r.status = 'running' AND (r.cancel_requested OR NOT EXISTS (SELECT 1 FROM machine_links l WHERE l.id = r.link_id AND l.expires_at > now())),
                    r.log_bytes
                 FROM test_runs r WHERE r.id = $1 AND r.machine_install = $2
                    AND (r.status = 'running' OR ($3 AND r.status = 'lost' AND r.ended_at > now() - {HELD_BACK_KEPT}))"
            ),
            &[&id, &me, &held_back],
        )
        .await
        .map_err(internal_error)?
        // Ended, or lost because its lease ran out. Either way, it's not this PC's to go on with.
        .ok_or(StatusCode::GONE)?;
    Ok((row.get(0), row.get(1)))
}

pub async fn heartbeat(
    State(state): State<Arc<AppState>>,
    headers: HeaderMap,
    Path(id): Path<String>,
) -> Result<Json<KeepGoing>, StatusCode> {
    let me = run_caller(&state, &headers, &id).await?;
    settle_lost_runs(&state.db).await?;
    let (cancel, log_bytes) = running_run(&state.db, &id, &me, false).await?;
    state
        .db
        .execute(
            "UPDATE test_runs SET lease_until = now() + interval '2 minutes' WHERE id = $1 AND status = 'running'",
            &[&id],
        )
        .await
        .map_err(internal_error)?;
    Ok(Json(KeepGoing {
        cancel,
        truncated: log_bytes >= MAX_LOG_BYTES,
    }))
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LogBody {
    seq: i32,
    text: String,
    /// Kept on the test PC while this server turned it away, and sent once the PC had updated.
    #[serde(default)]
    held_back: bool,
}

/// Adds to a run's log. Sending a part again is harmless, as each is numbered.
pub async fn append_log(
    State(state): State<Arc<AppState>>,
    headers: HeaderMap,
    Path(id): Path<String>,
    Json(body): Json<LogBody>,
) -> Result<Json<KeepGoing>, StatusCode> {
    let me = run_caller(&state, &headers, &id).await?;
    if body.seq < 0 || body.text.len() > MAX_LOG_CHUNK_BYTES || body.text.contains('\0') {
        return Err(StatusCode::BAD_REQUEST);
    }
    let (cancel, log_bytes) = running_run(&state.db, &id, &me, body.held_back).await?;
    let size = body.text.len() as i64;
    if log_bytes + size > MAX_LOG_BYTES {
        return Ok(Json(KeepGoing {
            cancel,
            truncated: true,
        }));
    }
    let added = state
        .db
        .execute(
            "INSERT INTO test_run_logs (run_id, seq, text) VALUES ($1, $2, $3) ON CONFLICT DO NOTHING",
            &[&id, &body.seq, &body.text],
        )
        .await
        .map_err(internal_error)?;
    if added > 0 {
        state
            .db
            .execute(
                "UPDATE test_runs SET log_bytes = log_bytes + $2,
                    lease_until = CASE WHEN status = 'running' THEN now() + interval '2 minutes' END
                 WHERE id = $1 AND status IN ('running', 'lost')",
                &[&id, &size],
            )
            .await
            .map_err(internal_error)?;
    }
    Ok(Json(KeepGoing {
        cancel,
        truncated: false,
    }))
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct EndBody {
    status: String,
    #[serde(default)]
    exit_code: Option<i32>,
    /// How each check went, when the repo names a results file. Each name once.
    #[serde(default)]
    checks: Vec<CheckBody>,
    /// Kept on the test PC while this server turned it away, and sent once the PC had updated, so
    /// a run called lost meanwhile still ends as it went (#124).
    #[serde(default)]
    held_back: bool,
}

#[derive(Deserialize, Serialize)]
pub struct CheckBody {
    name: String,
    outcome: String,
}

/// The target says how it went, and how each check went if the repo's results file said.
pub async fn end_run(
    State(state): State<Arc<AppState>>,
    headers: HeaderMap,
    Path(id): Path<String>,
    Json(body): Json<EndBody>,
) -> Result<StatusCode, StatusCode> {
    let me = run_caller(&state, &headers, &id).await?;
    if !RUN_ENDINGS.contains(&body.status.as_str())
        || body.checks.len() > MAX_CHECKS
        || body.checks.iter().any(|check| {
            check.name.is_empty()
                || check.name.len() > MAX_CHECK_NAME_BYTES
                || check.name.contains('\0')
                || !CHECK_OUTCOMES.contains(&check.outcome.as_str())
        })
    {
        return Err(StatusCode::BAD_REQUEST);
    }
    let ended = state
        .db
        .execute(
            &format!(
                "UPDATE test_runs SET status = $3, exit_code = $4, ended_at = now(), lease_until = NULL,
                    held_back_at = CASE WHEN $5 THEN COALESCE(held_back_at, now()) ELSE held_back_at END
                 WHERE id = $1 AND machine_install = $2
                    AND (status = 'running' OR ($5 AND status = 'lost' AND ended_at > now() - {HELD_BACK_KEPT}))"
            ),
            &[&id, &me, &body.status, &body.exit_code, &body.held_back],
        )
        .await
        .map_err(internal_error)?;
    if ended == 0 {
        return Err(StatusCode::GONE);
    }
    if !body.checks.is_empty() {
        let (names, outcomes): (Vec<&str>, Vec<&str>) = body
            .checks
            .iter()
            .map(|check| (check.name.as_str(), check.outcome.as_str()))
            .unzip();
        state
            .db
            .execute(
                "INSERT INTO test_run_checks (run_id, name, outcome)
                 SELECT $1, name, outcome FROM UNNEST($2::text[], $3::text[]) AS given (name, outcome)
                 ON CONFLICT DO NOTHING",
                &[&id, &names, &outcomes],
            )
            .await
            .map_err(internal_error)?;
    }
    Ok(StatusCode::NO_CONTENT)
}

/// How each check went in a run, for either PC.
pub async fn run_checks(
    State(state): State<Arc<AppState>>,
    headers: HeaderMap,
    Path(id): Path<String>,
) -> Result<Json<Vec<CheckBody>>, StatusCode> {
    let me = caller(&state, &headers).await?;
    let visible = state
        .db
        .query_opt(
            "SELECT 1 FROM test_runs WHERE id = $1 AND (linker_install = $2 OR machine_install = $2)",
            &[&id, &me],
        )
        .await
        .map_err(internal_error)?;
    if visible.is_none() {
        return Err(StatusCode::NOT_FOUND);
    }
    let rows = state
        .db
        .query(
            "SELECT name, outcome FROM test_run_checks WHERE run_id = $1 ORDER BY name",
            &[&id],
        )
        .await
        .map_err(internal_error)?;
    Ok(Json(
        rows.iter()
            .map(|row| CheckBody {
                name: row.get(0),
                outcome: row.get(1),
            })
            .collect(),
    ))
}

#[derive(Deserialize)]
pub struct CheckHistoryQuery {
    project: String,
    #[serde(default)]
    name: Option<String>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CheckHistoryRow {
    name: String,
    outcome: String,
    run_id: String,
    machine: String,
    machine_label: String,
    commit: String,
    created_at: f64,
}

/// A project's checks across the runs this PC sent or ran, newest first: one check's whole
/// history with a name, or else every check that failed at least once, with its passes too, so
/// the PC can tell one that flips from one that's broken. What counts as flipping is the PC's.
pub async fn check_history(
    State(state): State<Arc<AppState>>,
    headers: HeaderMap,
    Query(query): Query<CheckHistoryQuery>,
) -> Result<Json<Vec<CheckHistoryRow>>, StatusCode> {
    let me = caller(&state, &headers).await?;
    if query.project.is_empty() || query.project.len() > 128 {
        return Err(StatusCode::BAD_REQUEST);
    }
    let most = if query.name.is_some() {
        MAX_ONE_CHECK_HISTORY_ROWS
    } else {
        MAX_CHECK_HISTORY_ROWS
    };
    let rows = state
        .db
        .query(
            &format!(
                "SELECT c.name, c.outcome, r.id, r.machine_install, {MACHINE_LABEL}, r.commit_sha,
                    EXTRACT(EPOCH FROM r.created_at)::float8
                 FROM test_run_checks c JOIN test_runs r ON r.id = c.run_id
                 WHERE (r.linker_install = $1 OR r.machine_install = $1) AND r.project_id = $2
                    AND CASE WHEN $3::text IS NULL THEN c.name IN (
                        SELECT f.name FROM test_run_checks f JOIN test_runs fr ON fr.id = f.run_id
                        WHERE (fr.linker_install = $1 OR fr.machine_install = $1) AND fr.project_id = $2 AND f.outcome = 'failed')
                    ELSE c.name = $3 END
                 ORDER BY r.created_at DESC, c.name LIMIT $4"
            ),
            &[&me, &query.project, &query.name, &most],
        )
        .await
        .map_err(internal_error)?;
    Ok(Json(
        rows.iter()
            .map(|row| CheckHistoryRow {
                name: row.get(0),
                outcome: row.get(1),
                run_id: row.get(2),
                machine: row.get(3),
                machine_label: row.get(4),
                commit: row.get(5),
                created_at: row.get(6),
            })
            .collect(),
    ))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn runs_listed_keeps_within_bounds() {
        assert_eq!(runs_listed(None), 30);
        assert_eq!(runs_listed(Some(100)), 100);
        assert_eq!(runs_listed(Some(5000)), 200);
        assert_eq!(runs_listed(Some(0)), 1);
        assert_eq!(runs_listed(Some(-3)), 1);
    }

    #[test]
    fn a_run_is_reported_from_the_release_it_was_taken_on_or_the_one_before() {
        // Taken on 1.4.0, with this server now on 1.6.0 after 1.5.0.
        assert!(release_may_report(
            Some("1.4.0"),
            Some("1.4.0"),
            Some("1.5.0")
        ));
        assert!(release_may_report(
            Some("1.5.0"),
            Some("1.4.0"),
            Some("1.5.0")
        ));
        assert!(!release_may_report(
            Some("1.3.0"),
            Some("1.4.0"),
            Some("1.5.0")
        ));
        // Taken before runs noted their release, or on a first start with nothing before.
        assert!(release_may_report(Some("1.5.0"), None, Some("1.5.0")));
        assert!(!release_may_report(Some("1.5.0"), None, None));
        assert!(!release_may_report(None, Some("1.4.0"), Some("1.5.0")));
    }
}
