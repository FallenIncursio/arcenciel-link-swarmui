# ArcEnCiel Link for SwarmUI

ArcEnCiel Link sends one-click model downloads from [arcenciel.io](https://arcenciel.io) into SwarmUI.

## Version 2.0

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
