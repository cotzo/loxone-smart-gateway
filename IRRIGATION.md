# Irrigation

The gateway reads weather sensor states and the selected Loxone Irrigation function block directly from its authenticated Loxone state subscription. It computes zone runtimes, synchronizes those durations back to Loxone, and maintains persisted weather observations, water balances, run history, and decision summaries.

## Setup

1. Save the Miniserver connection and refresh its discovered state catalog.
2. In Irrigation → Loxone Setup, choose the Miniserver and select the source state for each required weather measurement. Select the irrigation-required control state as well.
3. Select the Irrigation function block, preview its current zones, then import them. Review zone type, balance group, exposure, and measured application rate. Re-import preserves those zone settings and existing gateway IDs.
4. Select the block's active/current-zone states for automatic run tracking. The service observes active runs and calculates applied water without per-zone HTTP callbacks.
5. Set up the Lawn Dry control state. It is set based on the lawn-surface dryness model and mowing safety checks.

There is no separate weather commit: a complete set of selected sensor readings is committed automatically, with observations rate-limited to avoid duplicate samples from closely spaced state updates. When an input does not change, its latest known value remains available for subsequent complete observations.

## Decision model

For each balance group, the stored deficit is updated from reference evapotranspiration, zone exposure, and effective rainfall, clamped to the configured maximum. Forecast rain reduces the current recommendation but does not alter stored history. A zone runtime is derived from the water need and the combined application rate of all zones in its balance group, then capped by the configured maximum runtime. Incomplete or unavailable data results in a zero runtime.

The Irrigation Required control is on only when the weather data is complete and at least one zone requires non-zero irrigation; otherwise it is off. The Loxone irrigation block receives the calculated duration per zone. It is set to zero when data is incomplete or no watering is recommended.

The Lawn Dry control reflects the independent surface-wetness model. It is on only when the latest weather is fresh, estimated surface wetness is below the configured limit, no recent rain or active lawn irrigation blocks mowing, and the heavy-rain lockout has expired. A future rain forecast alone does not make currently dry grass wet.

## Status and diagnostics

Irrigation → Status shows saved observations, staged sensor values, stored water balances, active zones, recommended runtimes, run history, and the rationale for irrigation and Lawn Dry decisions. Use the Config and Zones tabs to adjust settings. No legacy `/Irrigation/...` HTTP inputs, virtual weather-output commands, explicit commits, or per-zone applied-run callbacks are part of the current setup.

The authenticated UI API is rooted at `/api`; the irrigation dashboard is `GET /api/irrigation/status`, and block discovery/preview/import use `/api/irrigation/loxone/blocks`, `/preview`, and `/import`.
