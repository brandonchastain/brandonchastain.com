#!/usr/bin/env bash
# One-shot: for each tracked trip, run find-trip-photos and then regenerate the trip report.
set -e
FP=.github/skills/find-trip-photos/.venv/Scripts/python.exe
FS=.github/skills/find-trip-photos/scripts/find_photos.py
TP=.github/skills/tripreport/.venv/Scripts/python.exe
TS=.github/skills/tripreport/scripts/generate.py

# slug | name | region | distance | gain | moving
TRIPS=(
"snoqualmie_mtn|Snoqualmie Mountain|Alpine Lakes Wilderness, Mt. Baker-Snoqualmie NF, WA|3.09|2984|4h 16m"
"eldorado_2|Eldorado Peak (2023)|North Cascades National Park, WA|9.68|6674|10h 33m"
"eldorado_1|Eldorado Peak|North Cascades National Park, WA|6.52|4159|6h 13m"
"track-21024-120420pm|Mt Si|Mt. Si Natural Resources Conservation Area, WA|8.31|3253|4h 19m"
"track-51323-120101pm|Little Si|Mt. Si Natural Resources Conservation Area, WA|4.99|1093|2h 6m"
"track-51323-32841pm|Rattlesnake Ledge|Rattlesnake Mountain Scenic Area, WA|4.21|936|1h 39m"
"track-7823-104316am|Klahhane Ridge|Olympic National Park, WA|7.12|2342|3h 9m"
"track-81923-102701am|Mt Rainier - Sunrise Area|Mt. Rainier National Park, WA|5.35|1017|2h 6m"
"track-93023-94843am|Tomyhoi Peak|Mt. Baker Wilderness, Mt. Baker-Snoqualmie NF, WA|11.7|3693|6h 53m"
"track-10723-92949am|Lake Ingalls|Alpine Lakes Wilderness, Okanogan-Wenatchee NF, WA|11.4|2889|6h 15m"
"track-51124-112204am|Oyster Dome|Blanchard State Forest, WA|5.90|1187|2h 19m"
"cascade-pass-sahale-arm|Cascade Pass and Sahale Arm|North Cascades National Park, WA|12.8|3991|8h 4m"
"mt-daniel-west-summit-with-will|Mt Daniel West Summit|Alpine Lakes Wilderness, Mt. Baker-Snoqualmie NF, WA|16.0|5074|9h 28m"
"track-82022-63134am|Enchantments Thru-Hike|Alpine Lakes Wilderness, Okanogan-Wenatchee NF, WA|22.9|5322|10h 57m"
"pilchuck|Mt Pilchuck|Mt. Pilchuck State Park, Mt. Baker-Snoqualmie NF, WA||"
"iron_peak|Iron Peak|Alpine Lakes Wilderness, Okanogan-Wenatchee NF, WA||"
"storm_king|Mount Storm King|Olympic National Park, WA|4.1|2100|"
)

for row in "${TRIPS[@]}"; do
  IFS='|' read -r slug name region d g m <<< "$row"
  echo
  echo "===== $slug ====="
  $FP -u $FS --data "tripreports/data/$slug" 2>&1 | tail -8
  args=(--data "tripreports/data/$slug" --name "$name" --region "$region")
  [[ -n "$d" ]] && args+=(--gaia-distance "$d")
  [[ -n "$g" ]] && args+=(--gaia-gain "$g")
  [[ -n "$m" ]] && args+=(--gaia-moving "$m")
  $TP $TS "${args[@]}" 2>&1 | tail -3
done
