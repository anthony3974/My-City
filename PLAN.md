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
11. **Plus-sign wheels.** Two crossed spokes ("+") on each wheel instead of the single bar, so the spin reads better (taxi in `PlayerCar.cs`, maybe traffic too).
12. **Car choice or paint colour**, and a pause and settings menu (graphics quality, touch layout).
13. **Tilt steering** as an option on phones.
14. **Visible traffic lights.** The traffic already obeys lights; they just aren't drawn.

## Progression
15. **Slower starting car.** Lower the base top speed and acceleration so there is room to grow.
16. **Less grip in corners at the start.** The car turns too easily now; start with weaker cornering so better handling is something you earn.
17. **Upgrades bought with taxi money.** Speed (top speed, acceleration), brakes (brake force) and handling (grip, steering), each in a few levels with rising prices, saved in `PlayerPrefs`.

## Playtest notes (22 Nov 2026)
18. **Blips while delivering.** While carrying a passenger, hide the blips of other people. They should not show the green highway tip above their heads during a delivery.
19. **Upgrade menu text on PC.** On hover, the text looks doubled or 3D. The handling and grip text show as two separate areas and get highlighted oddly. Check the whole screen for this, including the button and the speedometer text.
20. **Traffic around the player.** The minimap shows cars, but not enough are near the player. On a long straight drive there can be no car ahead. Show where more cars are needed.
21. **Drift is too easy.** A 90° turn takes a couple of seconds, so the handbrake acts like a U-turn button. Make drifts stretch over a longer corner, like modern games.
22. **Speed-sensitive steering.** Steering should get slower at high speed. Right now it is too fast to steer even on low handling.
23. **Car damage.** Visible damage and a damage bar. When it runs out, the car needs repair or replacement.
24. **Visible traffic lights.** Draw them even though they don't need to work properly, since the cars don't follow them. It makes the street corners look better. Ties to item 14.
25. **Floating trees.** The wood of the trunk is not connected to the top of the tree.
26. **Taxi wheel wells.** Add wheel wells so the wheels don't clip inside the taxi body.
27. **More detailed AI vehicles.** Some vans look very low detail. Add some depth and a bit of pattern design, without going overboard.
28. **Taxi light bar.** Add a light bar on top of the taxi, made from small blocks like the blank-text placeholder used on buildings. It does not need to spell "TAXI", for performance.
29. **Upgrade pricing.** The price curve feels good so far (the player has reached level 3). Keep it.
30. **Metrics ideas.** Add some in-game metrics, e.g. per 5 minutes:
    - Passengers delivered per 5 minutes
    - Money earned per minute
    - Average delivery time
    - Distance driven and top speed reached
    - Total and longest drift score
    - Crashes and damage taken
    - Nearby traffic density (ties to item 20)

## Done
Crash feedback (1), HUD dial and minimap (2), taxi fares (4), AI traffic (7), app icon (10), plus-sign wheels (11), slower starting car (15), weaker starting grip (16), upgrades (17).

## Suggested order
Checkpoint runs (3), then a pause and settings menu (12).
