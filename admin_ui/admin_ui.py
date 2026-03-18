# anti-scraping-defense-iis/admin_ui/admin_ui.py
# Modified for Windows/IIS Compatibility
# Flask application for the Admin Metrics Dashboard
# anti-scraping-defense-iis/admin_ui/admin_ui.py
# Modified for Windows/IIS Compatibility
# Flask application for the Admin Metrics Dashboard

import logging
import os
import sys

from flask import Flask, jsonify, render_template

# --- Define Windows Paths (REPLACE PLACEHOLDERS if needed) ---
# Define the base directory for your application on the Windows server
APP_BASE_DIR = os.getenv(
    "APP_BASE_DIRECTORY",
    r"C:\inetpub\wwwroot\anti_scraping_defense_iis",
)
LOGS_DIR = os.path.join(APP_BASE_DIR, "logs")
os.makedirs(LOGS_DIR, exist_ok=True)

# --- Setup Logging ---
logging.basicConfig(
    level=os.getenv("LOG_LEVEL", "INFO").upper(),
    format="%(asctime)s - %(name)s - %(levelname)s - %(message)s",
)
logger = logging.getLogger(__name__)

# --- Adjust Python Path ---
# Add parent directory to sys.path to find shared modules
# Assuming this script is in anti-scraping-defense-iis/admin_ui/
parent_dir = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
if parent_dir not in sys.path:
    sys.path.insert(0, parent_dir)
logger.debug("Parent directory added to sys.path: %s", parent_dir)

# --- Import Shared Metrics Module ---
try:
    import metrics

    METRICS_AVAILABLE = True
    logger.info("Metrics module imported successfully by Admin UI.")
except ImportError as error:
    logger.error(
        "ERROR: Could not import metrics module in Admin UI: %s. Metrics will be unavailable.",
        error,
        exc_info=True,
    )

    class MockMetrics:
        def get_metrics(self):
            return {
                "error": "Metrics module not available",
                "service_uptime_seconds": 0,
            }

        def start_metrics_scheduler(self):
            logger.warning("Metrics scheduler cannot start: metrics module unavailable.")

        def increment_metric(self, key: str, value: int = 1):
            return None

    metrics = MockMetrics()
    METRICS_AVAILABLE = False

# --- Flask App Setup ---
template_dir = os.path.join(os.path.dirname(os.path.abspath(__file__)), "templates")
if not os.path.isdir(template_dir):
    logger.warning(
        "Templates directory not found at default location relative to script: %s",
        template_dir,
    )

app = Flask(__name__, template_folder=template_dir)


# --- Start Metrics Scheduler ---
LOG_METRICS_TO_JSON = os.getenv("LOG_METRICS_TO_JSON", "false").lower() == "true"
if LOG_METRICS_TO_JSON and METRICS_AVAILABLE:
    logger.info("LOG_METRICS_TO_JSON is enabled. Starting metrics scheduler...")
    try:
        metrics.start_metrics_scheduler()
    except Exception as error:
        logger.error("Failed to start metrics scheduler: %s", error, exc_info=True)
else:
    logger.info(
        "JSON metrics logging is disabled or metrics module unavailable. Scheduler not started."
    )


# --- Routes ---
@app.route("/")
def index():
    """Serves the main dashboard HTML page."""
    logger.info("Serving admin dashboard page.")
    try:
        return render_template("index.html")
    except Exception as error:
        logger.error(
            "Error rendering template 'index.html': %s",
            error,
            exc_info=True,
        )
        return (
            "<h1>Error loading dashboard</h1><p>Could not render the admin template.</p>",
            500,
        )


@app.route("/metrics")
def metrics_endpoint():
    """Provides the current metrics as JSON."""
    if not METRICS_AVAILABLE:
        logger.warning("Metrics endpoint called, but metrics module is unavailable.")
        return jsonify({"error": "Metrics module unavailable"}), 500

    try:
        current_metrics = metrics.get_metrics()
        return jsonify(current_metrics)
    except Exception as error:
        logger.error("Error retrieving metrics: %s", error, exc_info=True)
        return jsonify({"error": "Failed to retrieve metrics"}), 500


# --- Hosting ---
if __name__ == "__main__":
    logger.warning(
        "Running Flask app directly using development server (for testing only)."
    )
    try:
        import waitress

        logger.info("Starting server with Waitress on http://127.0.0.1:5002")
        waitress.serve(app, host="127.0.0.1", port=5002)
    except ImportError:
        logger.warning(
            "Waitress not found. Falling back to Flask development server."
        )
        app.run(host="127.0.0.1", port=5002, debug=False)

# (Adjust paths and environment variables as needed)
