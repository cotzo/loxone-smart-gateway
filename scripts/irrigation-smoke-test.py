#!/usr/bin/env python3
"""Read-only smoke test for a deployed Loxone Smart Gateway irrigation API."""

from __future__ import annotations

import argparse
import json
import math
import sys
import threading
from dataclasses import dataclass
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any
from urllib.error import HTTPError, URLError
from urllib.parse import urljoin, urlparse
from urllib.request import ProxyHandler, Request, build_opener


EXPECTED_ZONE_IDS = {f"V{number}" for number in range(1, 7)}
SCALAR_FIELDS = {
    "/Irrigation/et0": "et0Observed24hMm",
    "/Irrigation/rain24h": "rain24hMm",
    "/Irrigation/rain72h": "rain72hMm",
    "/Irrigation/forecast-rain": "forecastRainMm",
    "/Irrigation/deficit": "waterDeficitMm",
}


@dataclass(frozen=True)
class Response:
    status: int
    body: str


class Report:
    def __init__(self) -> None:
        self.passed = 0
        self.failed = 0
        self.warnings = 0

    def check(self, condition: bool, message: str) -> None:
        if condition:
            self.passed += 1
            print(f"PASS  {message}")
        else:
            self.failed += 1
            print(f"FAIL  {message}")

    def warn(self, message: str) -> None:
        self.warnings += 1
        print(f"WARN  {message}")


class GatewayClient:
    def __init__(self, base_url: str, timeout_seconds: float) -> None:
        parsed = urlparse(base_url)
        if parsed.scheme not in {"http", "https"} or not parsed.netloc:
            raise ValueError("base URL must include http:// or https:// and a host")

        self.base_url = base_url.rstrip("/") + "/"
        self.timeout_seconds = timeout_seconds
        # Never send LAN gateway traffic through a configured HTTP proxy.
        self.opener = build_opener(ProxyHandler({}))

    def get(self, path: str) -> Response:
        url = urljoin(self.base_url, path.lstrip("/"))
        request = Request(
            url,
            method="GET",
            headers={"Accept": "application/json", "User-Agent": "loxone-irrigation-smoke-test/1"},
        )
        try:
            with self.opener.open(request, timeout=self.timeout_seconds) as response:
                return Response(response.status, response.read().decode("utf-8"))
        except HTTPError as error:
            return Response(error.code, error.read().decode("utf-8"))


def is_finite_number(value: Any) -> bool:
    return (
        isinstance(value, (int, float))
        and not isinstance(value, bool)
        and math.isfinite(float(value))
    )


def parse_json(response: Response, path: str, report: Report) -> Any | None:
    try:
        return json.loads(response.body)
    except json.JSONDecodeError:
        report.check(False, f"GET {path} returns valid JSON")
        return None


def check_number(
    report: Report,
    value: Any,
    label: str,
    *,
    minimum: float | None = None,
) -> None:
    valid = is_finite_number(value)
    if valid and minimum is not None:
        valid = float(value) >= minimum
    suffix = f" and is >= {minimum:g}" if minimum is not None else ""
    report.check(valid, f"{label} is a finite number{suffix}")


def check_close(report: Report, actual: Any, expected: Any, label: str) -> None:
    valid = is_finite_number(actual) and is_finite_number(expected)
    if valid:
        valid = math.isclose(float(actual), float(expected), abs_tol=0.011)
    report.check(valid, f"{label} matches the aggregate result")


def run_suite(base_url: str, timeout_seconds: float, *, show_summary: bool = True) -> int:
    report = Report()
    client = GatewayClient(base_url, timeout_seconds)

    health = client.get("/health")
    report.check(health.status == 200, "GET /health returns HTTP 200")
    report.check(health.body.strip().lower() == "healthy", "GET /health reports Healthy")

    aggregate_response = client.get("/Irrigation")
    report.check(aggregate_response.status == 200, "GET /Irrigation returns HTTP 200")
    aggregate = parse_json(aggregate_response, "/Irrigation", report)
    if not isinstance(aggregate, dict):
        report.check(False, "GET /Irrigation returns a JSON object")
        return finish(report)
    report.check(True, "GET /Irrigation returns a JSON object")

    required_fields = {
        "irrigate",
        "localDataComplete",
        "et0Observed24hMm",
        "rain24hMm",
        "rain72hMm",
        "forecastRainMm",
        "forecastEt0Mm",
        "waterDeficitMm",
        "zones",
        "calculatedAt",
    }
    report.check(required_fields <= aggregate.keys(), "aggregate result contains every expected field")
    report.check(type(aggregate.get("irrigate")) is bool, "irrigate is boolean")
    report.check(type(aggregate.get("localDataComplete")) is bool, "localDataComplete is boolean")

    for field in (
        "et0Observed24hMm",
        "rain24hMm",
        "rain72hMm",
        "forecastRainMm",
        "forecastEt0Mm",
        "waterDeficitMm",
    ):
        check_number(report, aggregate.get(field), field, minimum=0)

    if aggregate.get("localDataComplete") is False:
        report.check(aggregate.get("irrigate") is False, "incomplete local data disables irrigation")
        check_close(report, aggregate.get("waterDeficitMm"), 0, "incomplete-data water deficit")

    forecast_rain = aggregate.get("forecastRainMm")
    forecast_et0 = aggregate.get("forecastEt0Mm")
    if forecast_rain == 0 and forecast_et0 == 0:
        report.warn(
            "forecast rain and ET0 are both zero; this can be valid, but it is also the "
            "fallback used when Open-Meteo is unavailable—inspect container logs"
        )
    else:
        report.check(True, "forecast integration returned a non-zero rain or ET0 value")

    zones = aggregate.get("zones")
    report.check(isinstance(zones, list), "zones is a JSON array")
    zone_by_id: dict[str, dict[str, Any]] = {}
    if isinstance(zones, list):
        for zone in zones:
            if isinstance(zone, dict) and isinstance(zone.get("id"), str):
                zone_by_id[zone["id"]] = zone
        report.check(set(zone_by_id) == EXPECTED_ZONE_IDS, "aggregate contains zones V1 through V6 exactly once")
        for zone_id in sorted(EXPECTED_ZONE_IDS):
            zone = zone_by_id.get(zone_id)
            if zone is None:
                continue
            check_number(report, zone.get("requiredMm"), f"{zone_id}.requiredMm", minimum=0)
            runtime = zone.get("runtimeSeconds")
            report.check(
                isinstance(runtime, int) and not isinstance(runtime, bool) and runtime >= 0,
                f"{zone_id}.runtimeSeconds is a non-negative integer",
            )
            if aggregate.get("irrigate") is False:
                check_close(report, zone.get("requiredMm"), 0, f"{zone_id}.requiredMm while irrigation is disabled")
                report.check(runtime == 0, f"{zone_id}.runtimeSeconds is zero while irrigation is disabled")

    irrigate_response = client.get("/Irrigation/irrigate")
    report.check(irrigate_response.status == 200, "GET /Irrigation/irrigate returns HTTP 200")
    irrigate_scalar = parse_json(irrigate_response, "/Irrigation/irrigate", report)
    report.check(irrigate_scalar in (0, 1), "irrigate scalar is 0 or 1")
    report.check(irrigate_scalar == int(bool(aggregate.get("irrigate"))), "irrigate scalar matches aggregate result")

    complete_response = client.get("/Irrigation/data-complete")
    report.check(complete_response.status == 200, "GET /Irrigation/data-complete returns HTTP 200")
    complete_scalar = parse_json(complete_response, "/Irrigation/data-complete", report)
    report.check(complete_scalar in (0, 1), "data-complete scalar is 0 or 1")
    report.check(
        complete_scalar == int(bool(aggregate.get("localDataComplete"))),
        "data-complete scalar matches aggregate result",
    )

    for path, field in SCALAR_FIELDS.items():
        response = client.get(path)
        report.check(response.status == 200, f"GET {path} returns HTTP 200")
        scalar = parse_json(response, path, report)
        check_close(report, scalar, aggregate.get(field), f"GET {path}")

    for zone_id in sorted(EXPECTED_ZONE_IDS):
        path = f"/Irrigation/zone/{zone_id}"
        response = client.get(path)
        report.check(response.status == 200, f"GET {path} returns HTTP 200")
        runtime = parse_json(response, path, report)
        report.check(
            isinstance(runtime, int) and not isinstance(runtime, bool) and runtime >= 0,
            f"GET {path} returns a non-negative integer",
        )
        aggregate_zone = zone_by_id.get(zone_id)
        if aggregate_zone is not None:
            report.check(
                runtime == aggregate_zone.get("runtimeSeconds"),
                f"GET {path} matches the aggregate runtime",
            )

    unknown_zone = client.get("/Irrigation/zone/UNKNOWN")
    report.check(unknown_zone.status == 404, "GET /Irrigation/zone/UNKNOWN returns HTTP 404")

    if show_summary:
        print("\nObserved irrigation result:")
        print(json.dumps(aggregate, indent=2, sort_keys=True))
    return finish(report)


def finish(report: Report) -> int:
    print(
        f"\nRESULT: {report.passed} passed, {report.failed} failed, "
        f"{report.warnings} warning(s)"
    )
    return 1 if report.failed else 0


SELF_TEST_RESULT = {
    "irrigate": False,
    "localDataComplete": False,
    "et0Observed24hMm": 0,
    "rain24hMm": 0.6,
    "rain72hMm": 1.2,
    "forecastRainMm": 0.4,
    "forecastEt0Mm": 2.1,
    "waterDeficitMm": 0,
    "zones": [
        {"id": zone_id, "requiredMm": 0, "runtimeSeconds": 0}
        for zone_id in sorted(EXPECTED_ZONE_IDS)
    ],
    "calculatedAt": "2026-08-18T19:00:00+00:00",
}


class SelfTestHandler(BaseHTTPRequestHandler):
    def do_GET(self) -> None:  # noqa: N802 - BaseHTTPRequestHandler API
        scalars: dict[str, Any] = {
            "/Irrigation/irrigate": 0,
            "/Irrigation/data-complete": 0,
            **{path: SELF_TEST_RESULT[field] for path, field in SCALAR_FIELDS.items()},
        }
        if self.path == "/health":
            self.respond(200, "Healthy", "text/plain")
        elif self.path == "/Irrigation":
            self.respond(200, json.dumps(SELF_TEST_RESULT))
        elif self.path in scalars:
            self.respond(200, json.dumps(scalars[self.path]))
        elif self.path.startswith("/Irrigation/zone/"):
            zone_id = self.path.rsplit("/", 1)[-1]
            self.respond(200 if zone_id in EXPECTED_ZONE_IDS else 404, "0" if zone_id in EXPECTED_ZONE_IDS else "")
        else:
            self.respond(404, "")

    def respond(self, status: int, body: str, content_type: str = "application/json") -> None:
        encoded = body.encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(encoded)))
        self.end_headers()
        self.wfile.write(encoded)

    def log_message(self, format: str, *args: Any) -> None:
        return


def run_self_test() -> int:
    server = ThreadingHTTPServer(("127.0.0.1", 0), SelfTestHandler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        host, port = server.server_address
        return run_suite(f"http://{host}:{port}", 2, show_summary=False)
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=2)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Run GET-only health and irrigation checks. This script never calls "
            "POST /Irrigation/weather and cannot modify irrigation history."
        )
    )
    parser.add_argument("base_url", nargs="?", help="gateway base URL, for example http://192.168.1.200:91")
    parser.add_argument("--timeout", type=float, default=15, help="per-request timeout in seconds (default: 15)")
    parser.add_argument("--self-test", action="store_true", help="validate this smoke test against its built-in stub")
    args = parser.parse_args()
    if not args.self_test and not args.base_url:
        parser.error("base_url is required unless --self-test is used")
    if args.timeout <= 0:
        parser.error("--timeout must be greater than zero")
    return args


def main() -> int:
    args = parse_args()
    try:
        return run_self_test() if args.self_test else run_suite(args.base_url, args.timeout)
    except (URLError, TimeoutError, ConnectionError, ValueError) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
