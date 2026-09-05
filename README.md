# ArcEnCiel Link for SwarmUI

ArcEnCiel Link sends one-click model downloads from [arcenciel.io](https://arcenciel.io) into SwarmUI.

## Version 2.3

- Link Keys (`lk_...`) are the only supported Link credential.
- Private downloads use a short-lived header grant bound to the configured ArcEnCiel HTTPS origin; redirects are refused.
- Worker enablement persists across SwarmUI restarts.
- Checkpoint, LoRA, VAE, embedding, GGUF, and `.sft` files participate in hourly full inventory scans.
- Local routes support browser Private Network Access and retain SwarmUI permission checks for the settings UI.

## Installation

Clone the repository into:

```text
SwarmUI/src/Extensions/ArcEnCielLink
```

Restart SwarmUI. The included `ArcEnCielLinkExtension.csproj` lets SwarmUI compile the extension against its own reference assembly. Keep `ArcEnCielLinkExtension.cs` at the extension root.

## Connect

1. Start SwarmUI with the extension installed.
2. Open the ArcEnCiel Link panel on [arcenciel.io](https://arcenciel.io).
3. Generate or select a Link Key and press **Connect**.
4. Assign the detected SwarmUI endpoint or use **Custom...** for a non-standard loopback port.

The advanced fallback is the `ArcEnCiel Link` server-settings card in SwarmUI.

## Hosted configuration (2.1.0)

All three Link workers use the same runtime variables. Set them **before starting the host**:

| Variable                 | Meaning                                                                    |
| ------------------------ | -------------------------------------------------------------------------- |
| `ARCENCIEL_LINK_URL`     | HTTPS API base; normally `https://link.arcenciel.io/api/link`.             |
| `ARCENCIEL_LINK_KEY`     | Dedicated Link Key from Colab Secrets or your runtime secret store.        |
| `ARCENCIEL_LINK_ENABLED` | Startup preference: `1/true/yes/on` or `0/false/no/off`, case-insensitive. |

Explicit environment variables override saved desktop settings, including explicitly empty values. An empty key never falls back to a saved key. Missing/invalid credentials or an invalid enabled value prevent automatic downloads and produce a value-free diagnostic. Environment values are not written to the extension config or OS keyring when you pause or resume; previous desktop settings remain available after removing the overrides.

Pause/resume works during the current process. On restart, `ARCENCIEL_LINK_ENABLED` takes effect again. Changing an environment-managed key through the browser is rejected: update the runtime secret and restart the host. Existing desktop installations with valid settings need no migration. Protocol 2 and the browser toggle payload are unchanged.

For an existing notebook host, use the [versioned Link setup notebook](https://github.com/FallenIncursio/arcenciel-link-webui/blob/v2.3.0/notebooks/ArcEnCiel_Link_Setup.ipynb). It supports WebUI/Forge, ComfyUI, and SwarmUI, validates the host checkout, installs the tagged extension, and loads Colab Secrets. It does not install a model or the host itself. Select **Remote / Colab** on the website and keep the bridge private. A health-probe log alone is not proof of an authenticated worker or a completed download.

## Configuration and security

Configuration is stored in `Data/Extensions/ArcEnCielLink/config.json`:

```json
{
  "BaseUrl": "https://link.arcenciel.io/api/link",
  "LinkKey": "",
  "Enabled": false,
  "MinFreeMb": 2048,
  "MaxRetries": 5,
  "BackoffBase": 2,
  "SaveHtmlPreview": false,
  "AllowPrivateOrigins": false
}
```

The file is written with user-only permissions where supported. Production URLs must use HTTPS and may not contain credentials, a query, or a fragment. `ARCENCIEL_DEV=1` permits local HTTP during development. Retired unknown fields are discarded the next time settings are saved.

## Local routes

- `GET /arcenciel-link/ping`
- `GET/POST /arcenciel-link/settings` (authenticated SwarmUI administrators)
- `POST /arcenciel-link/toggle_link`
- `GET /arcenciel-link/folders/{kind}`
- `POST /arcenciel-link/generate_sidecars`

## Development and release

CI checks out current SwarmUI, builds its reference assembly, and compiles the extension against .NET 8. A `vX.Y.Z` tag must match `src/ArcEnCielLinkProtocol.cs` and creates a GitHub Release asset.

The extension namespace intentionally does not begin with `SwarmUI`, because SwarmUI uses that prefix for built-in extensions.

## Troubleshooting

| Symptom                      | Check                                                                                          |
| ---------------------------- | ---------------------------------------------------------------------------------------------- |
| Worker offline               | Confirm a valid Link Key and enabled worker in the server-settings card.                       |
| Browser blocks local request | Accept the Private Network Access prompt; private development origins require explicit opt-in. |
| Download stays at 0%         | Check free disk space and Swarm model-directory permissions.                                   |
| SHA-256 mismatch             | Retry and check network or mirror stability.                                                   |

## License

[MIT](LICENSE)

## Reliable download attempts (2.2.0)

The worker negotiates `job_lease_v1` with ArcEnCiel. Each device accepts one reserved download at a time and
acknowledges the attempt before opening a file. A fresh runtime identity, periodic heartbeats and attempt IDs
prevent stale workers from changing completed or cancelled jobs. The server retries expired attempts at most
three times, then reports an actionable error. Restarting a lost transfer currently downloads the file again.

Cancel interrupts the stream, hash check and retry wait, cleans this attempt's partial file and confirms cleanup.
If cancellation arrives after the atomic file commit, the installed model stays on disk. Legacy server compatibility
is retained; automatic recovery requires the updated ArcEnCiel server. Keep each device on its own Link Key.

## Guided setup (2.3.0)

Open [Link Hub](https://arcenciel.io/link) to choose your host and location, create or import a device key, and verify the setup. Local discovery runs only when requested; another computer or Google Colab connects directly without a local browser scan. Existing keys and downloads remain compatible.

Workers advertise `setup_check_v1`. A setup check transfers a fixed 4 KiB file into the selected native model folder, flushes and reads it back, verifies SHA-256, and deletes the temporary file. The result is bound to one key and runtime; shared keys, paused or busy workers cannot complete the check. Stable errors explain storage, write, transfer and cleanup failures. The check does not install a model or alter download history. The `Finish setup` button appears only after verification.
