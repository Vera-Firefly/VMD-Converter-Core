# Humanoid response profiles

The solver embeds two portable response profiles:

| File | Skeleton configuration |
| --- | --- |
| `standard53.json.gz` | Without UpperChest or Jaw. |
| `standard55.json.gz` | With UpperChest and Jaw. |

Both profiles contain the standard muscle descriptors. Groups for absent bones are omitted. `OfflineRigFactory` selects and adapts a profile to the imported skeleton. Use `--profile` to supply an alternative JSON or JSON.gz profile.

Regular builds embed these files and do not require Unity. To regenerate the built-in profiles, run `scripts/Build-Profiles.ps1 -Unity <Unity.exe>` from the repository root. Use Avatar-specific calibration for custom Humanoid settings.
