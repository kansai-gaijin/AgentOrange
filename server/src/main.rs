mod devices;
mod sync;

use std::sync::{Arc, Mutex};

use axum::{
    Router,
    extract::State,
    http::{HeaderMap, HeaderValue, StatusCode},
    middleware::map_response,
    response::Response,
    routing::{get, patch, post},
};
use tokio_postgres::{Client, NoTls};

const DEFAULT_BIND: &str = "127.0.0.1:47100";

/// Set by the release build. Local builds use the version in Cargo.toml.
const VERSION: &str = match option_env!("AGENT_ORANGE_VERSION") {
    Some(version) => version,
    None => env!("CARGO_PKG_VERSION"),
};

pub struct AppState {
    pub db: Client,
    /// None turns pairing off, for a server that hasn't been given a key yet.
    pub pairing_key: Option<String>,
    pub pairing_limit: Mutex<devices::PairingLimit>,
}

#[tokio::main]
async fn main() {
    let bind = std::env::var("AGENT_ORANGE_BIND").unwrap_or_else(|_| DEFAULT_BIND.to_string());
    let database_url = std::env::var("DATABASE_URL").expect("DATABASE_URL must be set");
    let pairing_key = std::env::var("AGENT_ORANGE_PAIRING_KEY")
        .ok()
        .filter(|key| !key.is_empty());
    if pairing_key.is_none() {
        println!("No pairing key is set, so new devices can't pair.");
    }

    let (db, connection) = tokio_postgres::connect(&database_url, NoTls)
        .await
        .expect("could not connect to the database");
    tokio::spawn(async move {
        // Without its database the server is no use, so exit and let systemd restart it.
        if let Err(error) = connection.await {
            eprintln!("Lost the database connection: {error}");
            std::process::exit(1);
        }
    });
    devices::create_tables(&db)
        .await
        .expect("could not set up the database");
    sync::create_tables(&db)
        .await
        .expect("could not set up the database");

    let state = Arc::new(AppState {
        db,
        pairing_key,
        pairing_limit: Mutex::new(devices::PairingLimit::default()),
    });

    let app = Router::new()
        .route("/v1/health", get(health))
        .route("/v1/pair", post(devices::pair))
        .route("/v1/version", get(version))
        .route("/v1/sync", post(sync::sync))
        .route("/v1/devices", get(devices::list))
        .route(
            "/v1/devices/{id}",
            patch(devices::rename).delete(devices::remove),
        )
        .layer(map_response(mark_as_ours))
        .with_state(state);

    let listener = tokio::net::TcpListener::bind(&bind)
        .await
        .unwrap_or_else(|error| panic!("could not listen on {bind}: {error}"));
    println!("Agent Orange server listening on {bind}");

    axum::serve(listener, app)
        .with_graceful_shutdown(async {
            let _ = tokio::signal::ctrl_c().await;
        })
        .await
        .expect("server stopped unexpectedly");
}

/// Clients only give up a login when told to by this server, and not by a proxy on the way to it.
async fn mark_as_ours(mut response: Response) -> Response {
    response
        .headers_mut()
        .insert("x-agent-orange", HeaderValue::from_static("1"));
    response
}

async fn health() -> &'static str {
    "🍊"
}

/// Only for paired devices. The public health check stays just an orange.
async fn version(
    State(state): State<Arc<AppState>>,
    headers: HeaderMap,
) -> Result<&'static str, StatusCode> {
    devices::authenticate(&state, &headers).await?;
    Ok(VERSION)
}
