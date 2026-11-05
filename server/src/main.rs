use axum::{Router, routing::get};

const DEFAULT_BIND: &str = "127.0.0.1:47100";

#[tokio::main]
async fn main() {
    let bind = std::env::var("AGENT_ORANGE_BIND").unwrap_or_else(|_| DEFAULT_BIND.to_string());
    let app = Router::new().route("/v1/health", get(health));

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

async fn health() -> &'static str {
    "🍊"
}
