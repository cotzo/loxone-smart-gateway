# Lawn Dry control

The gateway computes lawn-surface dryness independently from irrigation soil-water deficit. It updates the selected Loxone `Lawn Dry` control automatically; no HTTP polling endpoint or “mowing allowed” virtual input is required.

The control is on only when weather is fresh, estimated surface wetness is at or below the configured threshold, recent rain is absent, no configured lawn zone is active, and the heavy-rain lockout is inactive. It is off if any safety check fails. Forecast rain does not block mowing when the lawn is currently dry.

Surface wetness increases from measured rain and observed physical runs on zones marked `Lawn` (including irrigation, rinse, and manual runs). Weather observations drive drying; solar radiation, temperature, wind, and low relative humidity increase the drying estimate. Large observation gaps are not assumed to be drying time, keeping the result conservative.

Configure the selected output state and model thresholds in Irrigation → Loxone Setup and Config. The Irrigation → Status page explains each current pass/block decision and reports the estimated wetness.
