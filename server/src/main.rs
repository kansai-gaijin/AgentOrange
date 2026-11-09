mod devices;
mod sync;

use std::sync::{Arc, Mutex};

use axum::{
    Extension, Router,
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
    /// Only syncs use this one, one at a time, as each is a transaction.
    pub sync_db: tokio::sync::Mutex<Client>,
    pub high_water: sync::HighWater,
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

    let db = connect(&database_url).await;
    let sync_db = connect(&database_url).await;
    devices::create_tables(&db)
        .await
        .expect("could not set up the database");
    sync::create_tables(&db)
        .await
        .expect("could not set up the database");

    let state = Arc::new(AppState {
        db,
        sync_db: tokio::sync::Mutex::new(sync_db),
        high_water: sync::HighWater::load(),
        pairing_key,
        pairing_limit: Mutex::new(devices::PairingLimit::default()),
    });

    let app = |route: devices::Route| {
        Router::new()
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
            .layer(Extension(route))
            .with_state(state.clone())
    };

    // The public route gets its own address, as only there does Cloudflare say who's calling.
    // On the other, anyone on the tailnet could claim to be anyone.
    let local = serve(&bind, app(devices::Route::Local));
    match std::env::var("AGENT_ORANGE_PUBLIC_BIND")
        .ok()
        .filter(|bind| !bind.is_empty())
    {
        Some(public_bind) => {
            let public = serve(&public_bind, app(devices::Route::Public));
            tokio::try_join!(local, public).expect("server stopped unexpectedly");
        }
        None => local.await.expect("server stopped unexpectedly"),
    }
}

async fn serve(bind: &str, app: Router) -> std::io::Result<()> {
    let listener = tokio::net::TcpListener::bind(bind)
        .await
        .unwrap_or_else(|error| panic!("could not listen on {bind}: {error}"));
    println!("Agent Orange server listening on {bind}");
    axum::serve(listener, app)
        .with_graceful_shutdown(shutdown_requested())
        .await
}

async fn connect(database_url: &str) -> Client {
    let (db, connection) = tokio_postgres::connect(database_url, NoTls)
        .await
        .expect("could not connect to the database");
    tokio::spawn(async move {
        // Without its database the server is no use, so exit and let systemd restart it.
        if let Err(error) = connection.await {
            eprintln!("Lost the database connection: {error}");
            std::process::exit(1);
        }
    });
    db
}

/// systemd stops a service with SIGTERM, so a restart finishes the requests under way.
async fn shutdown_requested() {
    #[cfg(unix)]
    {
        use tokio::signal::unix::{SignalKind, signal};
        let mut terminate = signal(SignalKind::terminate()).expect("could not listen for SIGTERM");
        tokio::select! {
            _ = tokio::signal::ctrl_c() => {}
            _ = terminate.recv() => {}
        }
    }
    #[cfg(not(unix))]
    {
        let _ = tokio::signal::ctrl_c().await;
    }
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
