# Team Color System

## Goal

Each player should receive one unique team color when they join a match. Every survivor owned by that player should show that color on dedicated visual marker renderers, such as armbands or headbands.

The first target prefab is `Assets/Prefabs/BlockSurvivor.prefab`, where these child objects are the team-color markers:

- `Armband_L`
- `Armband_R`
- `HeadBand`

Neutral recruitable survivors should use a separate neutral material until they join a player team.

## Material Assets

Team color materials live in:

```text
Assets/Materials/TeamMaterials/
```

Current materials:

```text
BlueTeamMaterial.mat
CyanTeamMaterial.mat
GreenTeamMaterial.mat
OrangeTeamMaterial.mat
PurpleTeamMaterial.mat
RedTeamMaterial.mat
YellowTeamMaterial.mat
NeutralTeamMaterial.mat
```

`NeutralTeamMaterial` should not be assigned to player teams. It is reserved for future neutral recruitable survivors.

The block-survivor marker objects should use `NeutralTeamMaterial` in the prefab by default. This makes neutral/recruitable survivors visually correct without any runtime setup, and gives player-owned survivors a safe fallback color until their replicated team data is available.

Runtime code should replace the renderer material reference with the selected team material once the survivor belongs to a player team.

## Network Model

Do not network `Material` references.

Instead, `PlayerData` in `Gameplay.cs` stores a small team-color index:

```csharp
public int TeamColorIndex;
```

The index maps into a local palette array. For example:

```text
0 -> RedTeamMaterial
1 -> BlueTeamMaterial
2 -> GreenTeamMaterial
...
```

Use a reserved value for no player team color, for example:

```csharp
public const int NeutralTeamColorIndex = -1;
```

Because `PlayerData` is a Fusion `INetworkStruct`, this is the only networked part of the team-color system. Once it is replicated, every client resolves the same `TeamColorIndex` into its local material palette and applies the right visual color without sending material assets over the network.

## Palette

The palette is implemented as a `ScriptableObject`:

```csharp
[CreateAssetMenu(menuName = "SimpleFPS/Team Color Palette")]
public sealed class TeamColorPalette : ScriptableObject
{
    public Material[] TeamMaterials;
    public Material NeutralMaterial;
}
```

`Gameplay` references one palette asset:

```csharp
public TeamColorPalette TeamColorPalette;
```

Keep player team materials and the neutral material separate so neutral is never accidentally assigned to a player.

## Team Color Assignment

When a player joins, state authority assigns a color before team spawn:

1. Build a set of already-used `TeamColorIndex` values from current `PlayerData`.
2. Choose one unused non-neutral material index.
3. Save it into the joining player's `PlayerData.TeamColorIndex`.
4. Spawn the player's starting survivors as usual.

The assignment starts from a random palette index and walks through the palette until it finds an unused non-neutral team material. If all colors are already used, it reuses the random starting color. The lobby should eventually prevent impossible team-color setups if unique colors are required.

## Visual Application

Survivors use a small visual component:

```csharp
public sealed class SurvivorTeamColorVisual : MonoBehaviour
{
    public Renderer[] TeamColorRenderers;
}
```

On `BlockSurvivor.prefab`, assign:

- `Armband_L` renderer.
- `Armband_R` renderer.
- `HeadBand` renderer.

The component:

1. Finds the owning `Survivor`.
2. Finds `Gameplay` through `SceneObjects`.
3. Reads `Gameplay.PlayerData[survivor.OwnerRef].TeamColorIndex`.
4. Resolves that index through `TeamColorPalette`.
5. Assigns the material to every renderer in `TeamColorRenderers`.
6. Caches the last applied index/material so it does not reassign every frame.

Use renderer material replacement, not material mutation. The team-color materials are shared assets and should not be edited per survivor.

If the survivor has no valid player color yet, it applies or keeps `NeutralMaterial`.

## Spawn Timing

Survivors know their owner through:

```csharp
survivor.OwnerRef
```

`OwnerRef` is set immediately after spawn on state authority and replicated to clients. The visual component must tolerate data not being available on the first local frame:

- If `SceneObjects` is not ready, do nothing and try again later.
- If `PlayerData` does not yet contain the owner, do nothing and try again later.
- If the palette is missing or the index is invalid, use or keep the neutral material.

This avoids startup-order errors when objects spawn before all local references are ready.

## Future Neutral Survivors

Neutral survivors should have:

```text
OwnerRef = none / invalid owner
TeamColorIndex = NeutralTeamColorIndex
```

Their visual component should apply `NeutralMaterial`.

When recruited:

1. Assign the survivor to the recruiting player's owner/team.
2. Give it the next `CharacterIndex` for that team.
3. Let the same visual component resolve the recruiting player's `TeamColorIndex`.
4. The marker materials change from neutral to the recruiting player's color.

The visual system should not need special recruitment code beyond reacting to owner/team data changes.

## Prefab Notes

The color marker renderers should be explicit references in `SurvivorTeamColorVisual`. Avoid finding objects by string names every frame.

Name-based auto-discovery can be useful as an editor helper later, but runtime should be simple and reliable.

For `BlockSurvivor.prefab`, set the default material on `Armband_L`, `Armband_R`, and `HeadBand` to `NeutralTeamMaterial`. Player team colors are applied by runtime code; neutral should be the authored prefab state.

The original `Survivor.prefab` can also support the same component later if the regular humanoid survivor gets visible team-color attachments.

## Unity Setup Checklist

1. Create a `TeamColorPalette` asset through `Create > SimpleFPS > Team Color Palette`.
2. Assign the seven player team materials to `TeamMaterials`.
3. Assign `NeutralTeamMaterial` to `NeutralMaterial`.
4. Assign the palette asset to the `Gameplay` component in the gameplay scene.
5. On `BlockSurvivor.prefab`, set `Armband_L`, `Armband_R`, and `HeadBand` to use `NeutralTeamMaterial` by default.
6. Add `SurvivorTeamColorVisual` to `BlockSurvivor.prefab`.
7. Assign the `Armband_L`, `Armband_R`, and `HeadBand` renderers to `TeamColorRenderers`.
8. Leave `MaterialSlot` at `0` unless one of those renderers uses multiple material slots.
9. Make sure `Gameplay.SurvivorPrefab` points to the survivor prefab variant you want to spawn.

## Implemented Files

```text
Assets/Scripts/Gameplay/Gameplay.cs
Assets/Scripts/Gameplay/TeamColorPalette.cs
Assets/Scripts/Survivor/SurvivorTeamColorVisual.cs
```

## Out Of Scope

- Team-color UI in lobby.
- Player-selected colors.
- Colorblind-safe palette validation.
- Runtime material creation or tinting.
- Neutral survivor recruitment implementation.
- Different colors per individual survivor.
