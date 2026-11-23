# My City: improvement plan

> **Note:** AI made the plan, not fully read by owner. Completed and playtested items (1, 2, 4, 6, 7, 10, 11, 15, 16, 17, 29) have been removed; full history is in `git log -p PLAN.md`.

## Goals
3. **Checkpoint runs.** A marker a few blocks away, a timer, and a saved best time (`PlayerPrefs`).
5. **Collectibles.** Coins or stars on the roads, placed from each tile's seed so they stay in the same spots.

## Living city
8. **Day and night cycle.** Lit windows (glow map on the facades) and streetlights.
9. **Variety.** Ramps, bridges, a highway or a river every few tiles.

## Polish
12. **Car choice or paint colour**, and a pause and settings menu (graphics quality, touch layout).
13. **Tilt steering** as an option on phones.

## Playtest notes (23 Nov 2026)
31. **Low-speed full stop.** When the vehicle is moving very slowly (e.g. ~1 km/h) with no throttle, bring it to a complete stop so it does not travel at 1 km/h forever.

## Built, not yet marked tested (from 22 Nov 2026 playtest)
14. **Visible traffic lights.** The traffic already obeys lights; they just aren't drawn. *(Ties to item 24.)*
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
30. **Metrics ideas.** Add some in-game metrics, e.g. per 5 minutes:
    - Passengers delivered per 5 minutes
    - Money earned per minute
    - Average delivery time
    - Distance driven and top speed reached
    - Total and longest drift score
    - Crashes and damage taken
    - Nearby traffic density (ties to item 20)

## Suggested order
Checkpoint runs (3), then a pause and settings menu (12).
