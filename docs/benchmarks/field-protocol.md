# Field benchmark protocol (spec §8)

One recording per trial. References go on a paper sheet during the session and into
`docs/benchmarks/field/<date>/references.csv` afterwards (format:
`tools/field/references_template.csv`). The recorder names files
`walldist_<yyyyMMdd_HHmmss>.csv`, so files sorted by name are in sheet order.

## Equipment
- OnePlus 13R with the Development APK; battery ≥ 50 %, not charging.
- Tape measure and a carpenter's square; masking tape to mark points on the floor.
- The paper sheet: one line per trial with point, trial, motion, the three tape readings and notes.

## Every trial
1. Force-stop the app and relaunch it, so each trial starts with an empty wall map
   ("independently initialised", spec §8). Wait until the floor prompt disappears.
2. Stand at the marked point. Hold the phone at chest height pointing at the **floor**
   (about 60° down). Press **● Record CSV**.
3. Raise the phone to level, aimed at the wall under test. The analysis starts the
   acquisition clock at this moment (first row within 20° of level).
4. Hold still for 10 s, then press **■ Stop**.
5. Tape from the camera position, perpendicular to each wall: the aimed wall, the left wall
   and the right wall. Write the three numbers on the sheet; leave a blank where a wall is
   absent (junction, doorway gap).

## Points (corridors first)
- Side walls at 0.5 / 1 / 1.5 / 2 / 3 m: stand facing along the corridor, aim at the end
  wall or down the corridor; the side readings are the ones under test.
- End wall at 1, 2, 3, 4, 5 m: aim at it.
- Lateral positions: centre line, and 0.5 m off-centre towards each wall.
- Include one junction and one doorway gap longer than 1 m (expected: that side `NoWallOnSide`).

## Walking trials
- Tape the corridor width once at a few places; enter the median as `ref_width_m`.
- Relaunch, track the floor, press Record, walk the straight corridor at normal pace on the
  centre line, phone upright, for at least 15 m. Stop. `motion = walking`.
- Repeat 0.5 m off-centre.

## Conditions
Plain painted walls; glossy tiled floors; skirting boards; doors; dim lighting; people
walking past. Write the condition in the `condition` column with short fixed words
(`plain-paint`, `glossy-floor`, `skirting`, `doors`, `dim`, `people`).

## Trials
Three per point and condition.

## Analysis
```bash
/c/msys64/ucrt64/bin/python tools/field/analyse_field.py Builds/logs/WallDistanceLogs \
    docs/benchmarks/field/<date>/references.csv --markdown docs/benchmarks/field/<date>/report.md
```
