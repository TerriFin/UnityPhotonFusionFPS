# Survivor Map Roster UI

## Goal

Add a survivor roster next to the full-screen map so the player can quickly inspect, select, and configure their team.

The roster is a tactical control surface for owned survivors:

- show each survivor's health and awareness status,
- show which survivor is currently possessed,
- mirror the map selection state,
- allow survivor selection from UI cards,
- expose per-survivor AI behavior toggles,
- expose bulk behavior toggles for the current selection or whole team.

This should extend the existing map system from `Docs/MapSystemPhase2.md`. It should not create a separate selection model or bypass the state-authority validation used by current map orders.

## UI Shape

The roster sits beside the map while the full-screen map is open.

Recommended hierarchy:

```text
GameUI
  MapView
    RawImage
    IconRoot
    SelectionBox
    AssignedAreaCircle
    HoverLinkOverlay
  SurvivorRosterPanel
    BulkToggleBar
    Scroll View
      Viewport
        Content
          SurvivorRosterEntry instances
```

The panel should be anchored to the side of the map. On narrow screens it can use one column; on wider screens it can use two columns. If there are more survivors than fit vertically, the roster scrolls.

Implemented scripts:

```text
Assets/Scripts/UI/Map/SurvivorRosterController.cs
Assets/Scripts/UI/Map/SurvivorRosterEntry.cs
Assets/Scripts/UI/Map/ResponsiveRosterGrid.cs
```

`GameMapView` has a `RosterController` reference. If a `SurvivorRosterController` exists under the map object, `GameMapView` wires it automatically. The controller can also create a plain runtime panel when `CreateRuntimeUIIfMissing` is enabled, which is useful for testing before making a polished prefab.

## Responsive Grid Setup

Unity's built-in `GridLayoutGroup` is good for arranging cards, but it does not automatically choose a good column count from available panel width. The clean first implementation should use a tiny helper component, for example `ResponsiveRosterGrid`, attached to the `Content` object.

Recommended serialized values:

```csharp
public RectTransform Viewport;
public GridLayoutGroup Grid;
public int MaxColumns = 2;
public float MinCardWidth = 220f;
public float CardHeight = 86f;
public Vector2 Spacing = new Vector2(6f, 6f);
public RectOffset Padding;
```

The component computes:

```text
available width = Viewport.rect.width - padding - spacing
columns = floor((available width + spacing.x) / (MinCardWidth + spacing.x))
columns = clamp(columns, 1, MaxColumns)
cell width = remaining width divided by columns
cell height = CardHeight
content height = rows * CardHeight + spacing + padding
```

This gives:

- 1 column on small panels,
- 2 columns when there is enough width,
- 5 to 20 visible rows depending on screen height,
- scrolling when the roster has more entries than visible rows.

Avoid recalculating this every frame. Rebuild on:

- map open,
- screen resize,
- survivor count changes,
- layout parent size changes.

## Editor Setup Guide

1. Open the `GameUI` prefab or scene object.
2. Add a `SurvivorRosterPanel` object beside the full-screen map content.
3. Anchor it to the right side of the map root. Give it a fixed or percentage-like width that works at your target resolutions.
4. Add a vertical layout:
   - top child: `BulkToggleBar`,
   - bottom child: `Scroll View`.
5. On the scroll view:
   - enable vertical scrolling,
   - disable horizontal scrolling,
   - use a `RectMask2D` on `Viewport`,
   - make `Content` stretch horizontally and sit at the top.
6. Add `GridLayoutGroup` to `Content`.
7. Add the planned `ResponsiveRosterGrid` helper to `Content` and assign:
   - `Viewport`,
   - `GridLayoutGroup`,
   - `MaxColumns = 2`,
   - `MinCardWidth = 220`,
   - `CardHeight = 86` to `104`, depending on how much gear space is reserved.
8. Create a `SurvivorRosterEntry` prefab with:
   - root `Button` or click handler,
   - background image,
   - border/outline image,
   - face image on the left,
   - name text at the top,
   - crown icon slot,
   - HP bar,
   - behavior toggle row,
   - future gear row placeholder.
9. Assign the entry prefab to the roster controller.
10. Add a `HoverLinkOverlay` as a transparent UI layer above the map and below blocking UI popups.

## Survivor Card

Each card represents one alive player-owned survivor.

Card contents:

```text
Face placeholder
Name
Crown / possessed icon
HP bar
Awareness border
Selection highlight
Behavior toggles
Future gear area
```

### Name

Use the survivor object's current name for the first pass.

Later this can use a generated survivor name from a text file or character profile asset.

### Face

Use one shared placeholder image for now. It can and should take color from the team color, the image will be so that it will modify only the relevant part.

The layout should reserve the left side for a future portrait so the UI does not need to be rebuilt when cosmetic faces are added.

### HP Bar

Use `Health.CurrentHealth / Health.MaxHealth`.

Recommended visual:

- green fill for current HP,
- red background/missing-health area,
- hide or gray out the card when the survivor dies.

Dead survivors should be removed from command selection. Whether dead cards remain briefly for feedback or disappear immediately can be decided during implementation, but the selection model must treat them as invalid.

### Awareness Border

The card border communicates the strongest directly sensed threat:

```text
red    = survivor directly sees at least one enemy survivor
yellow = survivor directly sees zombies only
normal = survivor sees no direct enemies
```

Enemy survivors take priority. If a survivor sees both zombies and enemy survivors, the border is red.

This should use the same direct sensor knowledge that map icons use. Do not run extra physics scans from the UI. The roster can read `CharacterSensor.GetDirectKnownEnemies(...)` and classify the returned `NetworkObject`s:

- `Survivor` owned by another player -> red,
- `ZombieCharacter` -> yellow,
- neutral survivor -> not a threat for this border unless future rules change.

### Possessed Survivor

The currently possessed survivor should show a crown icon or equivalent clear marker.

Movement/follow/defend command masks still exclude the possessed survivor. Fine-grained roster behavior toggles are allowed to include the possessed survivor, because they only update stored AI settings that matter when that survivor becomes uncontrolled.

- show the possessed survivor in the list,
- mark it with the crown,
- do not include it in map movement/follow/defend command masks,
- allow individual behavior toggles to update its stored settings.

## Selection Behavior

The roster and map must share one local selection state.

Selection source of truth should remain the map selection/controller layer, or be extracted into a small shared selection model used by both:

```text
GameMapSelectionController
  selected survivors
  builds CharacterMask128 for server requests
  validates local selectability

SurvivorRosterController
  displays selection
  forwards card clicks to the same selection model
```

Rules:

- left click an unselected roster card: replace the current selection with that survivor,
- left click an already selected roster card: remove that survivor from the current selection,
- shift-left click an unselected roster card while at least one survivor is already selected: add every selectable survivor between that card and the closest already selected card in roster/character-index order,
- selected roster cards are highlighted,
- map-selected survivors are highlighted in the roster,
- dead/off-team/invalid survivors cannot be selected,
- the possessed survivor is marked but not command-selectable for movement/follow/defend orders,
- right click outside the map does nothing,
- while the map is open, left clicking empty non-interactive UI/map space clears the current selection.

Roster card selection is single-select by default so the player can quickly focus one survivor from the list. Clicking an already selected card toggles it off. Shift-click range selection never clears the current selection; it only fills the gap between the clicked survivor and the closest selected survivor.

When a survivor is selected from the map, keyboard cycling, drag select, or the roster itself, the roster scrolls that survivor's card into view. During multi-selection this happens for every newly selected survivor, so the final scroll position follows the latest survivor added to the selection. Shift/Ctrl keyboard cycling remains active while the pointer is over the roster; only map pointer actions are blocked by the roster panel. Cycling fires on key release, not press, and only when zero or one survivor is selected. This leaves shift press available for roster range selection.

## Hover Link

When the mouse hovers a survivor card, draw a line from the card's bottom-right corner to that survivor's map icon.

Recommended implementation:

```text
SurvivorRosterEntry raises hover begin/end.
SurvivorRosterController asks GameMapIconController for the survivor icon RectTransform.
HoverLinkOverlay draws one UI line between the two screen/UI points.
```

Rules:

- draw only while the card is hovered,
- hide the line if the survivor has no visible map icon,
- do not make the line block pointer input,
- update the line every frame while visible because map panning/zooming and scrolling can move both endpoints.

The line can be a simple generated UI strip between two points. It does not need a special asset.

## Behavior Toggles

Each card has individual behavior toggles. The top `BulkToggleBar` has the same toggles and applies them to a group.

Initial non-combat toggles should map to current settings:

```text
CollectVisiblePickups
InvestigateSuspiciousStimuli
RecruitNeutralSurvivors
AllowCombatAIActivation (legacy field name; displayed as the combat movement toggle)
```

The combat movement toggle enables/disables AI combat movement against both enemy survivors and zombies. When disabled, survivors may still aim and shoot according to their weapon/fire mode, but they do not reposition for cover, range, or close-zombie retreat. Non-combat movement from player orders, investigation, pickup collection, recruiting, and assigned-area patrol remains separate.

The roster also needs one non-boolean combat mode control:

```text
Weapon/fire mode: Automatic / Prefer Strong Weapons / Prefer Pistol / Hold Fire
```

This is a four-state segmented or cycling control on every survivor card and in the bulk settings row. It must use an enum-valued authoritative request rather than the existing boolean `ESurvivorAISetting` path. See `Docs/SurvivorWeaponPreferenceAI.md`.

The old broad keyboard setting shortcuts (`I`, `O`, `K`, `L`) have been removed. AI behavior settings are controlled through the roster's individual and bulk toggles. The roster uses the finer-grained request path:

```csharp
public enum ESurvivorAISetting
{
    CollectVisiblePickups,
    InvestigateSuspiciousStimuli,
    RecruitNeutralSurvivors,
    AllowCombatAIActivation,
}

Gameplay.RequestMapAISetting(CharacterMask128 mask, ESurvivorAISetting setting, bool enabled)
SurvivorAICommandService.ApplySelectedTeamAISetting(PlayerRef owner, CharacterMask128 mask, ESurvivorAISetting setting, bool enabled)
```

This request path is now implemented. The UI still sends a local `CharacterMask128`, and state authority validates ownership, alive state, and the setting enum before applying the change. Unlike movement/follow/defend orders, individual AI setting toggles are allowed to include the currently possessed survivor.

State authority must validate the same things current map setting requests validate:

- sender owns the survivor,
- survivor is alive,
- survivor index is in the requested mask,
- the setting is valid.

Do not let the UI mutate survivor settings directly.

## Bulk Toggle Rules

The bulk toggle bar target set is:

```text
selected alive owned survivors, if selection is not empty
otherwise all alive owned survivors
```

The bulk toggle state reflects the majority value for that target set:

```text
true  if more survivors have the setting enabled than disabled
false if more survivors have the setting disabled than enabled
mixed if exactly tied, if the UI supports a mixed state ANSWER: does not support mixed state, the toggle is on/off. If tied, prefer OFF. (so that it is easier to toggle ON for s)
```

Unity `Toggle` has no built-in mixed state. A practical first pass:

- show on/off by majority,
- show a small dash/overlay or muted color when tied.

The bulk toggle visuals should be a stable snapshot of the current target set. They should not constantly live-update from deaths, AI state changes, or per-card toggle changes while the player is looking at them, because that can make a button change state just before the player clicks it.

Refresh the bulk toggle visuals only when:

- the map opens,
- the player changes the roster/map selection,
- the player clicks one of the bulk toggles, in which case the clicked toggle should immediately show the value the player chose.

If a selected survivor dies or otherwise becomes invalid, remove it from the command target mask as usual. Do not recompute the visible bulk toggle majority unless that removal also produces an explicit selection refresh in the UI. The goal is to keep the toggles in sync with deliberate selection changes, not with every background state change.

When the player clicks a bulk toggle, apply the clicked value to every survivor in the current target set.

## Data Refresh

The roster should be mostly event-driven, with light polling only for values that already change continuously.

Recommended cadence:

```text
Selection/crown/survivor list: immediate or map tick
HP bars: 5-10 Hz is enough
Awareness borders: same cadence as map icon awareness or map tick
Toggle states: immediate after local/server request plus refresh from survivor settings
Layout rebuild: only on survivor count or size changes
```

Avoid:

- instantiating/destroying every map open if pooling is easy,
- rebuilding the layout every frame,
- doing extra sensor scans from UI,
- networking UI-only state.

## Relation To Networking

Local UI state:

- card objects,
- scroll position,
- hover line,
- selected survivor display,
- majority toggle display.

Networked or state-authority-owned gameplay state:

- survivor ownership,
- alive/dead state,
- health,
- survivor AI settings,
- survivor transforms,
- sensor data already replicated/available locally.

Behavior changes must go through the same request/RPC path as current map commands.

## Implementation Plan

1. Create the roster UI document and prefab plan.
2. Add `SurvivorRosterController` as a child/controller for `GameMapView`.
3. Add `SurvivorRosterEntry` for one card.
4. Add `ResponsiveRosterGrid` for 1-2 column layout.
5. Populate cards from local player's alive survivors.
6. Mirror map selection into card highlights.
7. Allow card click selection through the existing map selection model.
8. Add HP bar, name, placeholder face, and crown marker.
9. Add awareness border from direct sensor known enemies.
10. Add bulk toggle bar with majority-state calculation.
11. Add individual toggle controls and state-authority setting requests.
12. Add hover line to the map icon.
13. Pool card entries and verify with 5, 20, 50, and 128 survivors.

## Acceptance Criteria

- The roster opens with the full-screen map.
- The roster uses 1 column on narrow widths and 2 columns when enough width is available.
- The roster scrolls when survivors exceed visible rows.
- Each alive owned survivor has one card.
- HP bars show current health clearly.
- The possessed survivor has a crown marker.
- Cards show red borders for enemy survivor awareness.
- Cards show yellow borders for zombie-only awareness.
- Card selection and map selection stay synchronized.
- Empty left click while map is open clears selection.
- Right click outside the map does not issue orders.
- Bulk toggles apply to selected survivors, or all alive owned survivors when none are selected.
- Bulk toggle visuals reflect the current majority state.
- Individual toggles change only that survivor's setting through validated state-authority requests.
- Hovering a card draws a line to the survivor's visible map icon.

## Future Space

The card layout should reserve room for future systems:

- survivor portrait variants,
- generated names,
- current weapons,
- ammo,
- carried loot,
- control groups,
- downed/dead/reviving state,
- special orders or garrison assignment.

Keep those as empty slots/placeholders for now. The first implementation should focus on selection, status, and AI behavior toggles.
