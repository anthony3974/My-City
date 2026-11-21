# My City: improvement plan

> **Note:** AI made the plan, not fully read by owner.

## Feel
1. **Crash feedback.** Camera shake, sparks or a short slowdown when the car hits a building.
2. **Better HUD.** A speedometer dial and a minimap, drawn from the road layout the city generator already knows.

## Goals
3. **Checkpoint runs.** A marker a few blocks away, a timer, and a saved best time (`PlayerPrefs`).
4. **Deliveries or taxi fares.** Pick up at A, drop off at B, earn money.
5. **Collectibles.** Coins or stars on the roads, placed from each tile's seed so they stay in the same spots.
6. **Drift score.** Points for long handbrake drifts.

## Living city
7. **AI traffic.** Simple cars that follow the lanes on the fixed road grid.
8. **Day and night cycle.** Lit windows (glow map on the facades) and streetlights.
9. **Variety.** Ramps, bridges, a highway or a river every few tiles.

## Polish
10. **App icon.** It still uses Unity's default.
11. **Car choice or paint colour**, and a pause and settings menu (graphics quality, touch layout).
12. **Tilt steering** as an option on phones.

## Progression
13. **Slower starting car.** Lower the base top speed and acceleration so there is room to grow.
14. **Less grip in corners at the start.** The car turns too easily now; start with weaker cornering so better handling is something you earn.
15. **Upgrades bought with taxi money.** Speed (top speed, acceleration), brakes (brake force) and handling (grip, steering), each in a few levels with rising prices, saved in `PlayerPrefs`.

## Done
Crash feedback (1), HUD dial and minimap (2), taxi fares (4), AI traffic (7).

## Suggested order
Progression (13–15), then checkpoint runs.
