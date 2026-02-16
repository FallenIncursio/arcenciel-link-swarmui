# ArcEnCiel Link - SwarmUI Extension

Bring your ArcEnCiel models straight into SwarmUI with one click. Includes
Link Key auth, remote worker control, inventory sync, and sidecar generation.

---

## Release Notes (latest)

- Updated onboarding to a **Connect-first** flow: with the extension installed and local UI running, open ArcEnCiel Link panel on [arcenciel.io](https://arcenciel.io) and click **Connect**.
- Documented auto-detect behavior for local endpoints: `127.0.0.1` / `localhost` on ports `7860`, `7861`, `7801`, `8000`, `8501`.
- Added explicit **Custom endpoint** fallback guidance for non-standard host/port setups.
- Corrected queue/progress wording: status is shown in the **ArcEnCiel Link panel** and local worker logs.
- Updated credential messaging: **Link Key (`lk_...`) is primary**; **API key is legacy/deprecated** for current websocket flow.
- Clarified Swarm settings as fallback/advanced path when auto-detect is not sufficient.

---

## Features

- One-click download from ArcEnCiel model cards.
- Model-aware routing for checkpoints, LoRAs, VAEs, and embeddings.
- Background worker with retry back-off, disk-space guard, and SHA-256 verify.
- Inventory sync so ArcEnCiel can skip models already installed locally.
- Optional preview PNG, `.json`, and HTML quick-view sidecars.

---

## Installation (SwarmUI)

1. Clone into:

```text
SwarmUI/src/Extensions/ArcEnCielLink
```

2. Rebuild SwarmUI (`launch-dev` or run the `update` script).

Important: `ArcEnCielLinkExtension.cs` must stay at the extension root so SwarmUI can detect and load the extension.

---

## First-time setup (Connect-first)

1. Start SwarmUI with the extension installed.
2. On [arcenciel.io](https://arcenciel.io) open the **ArcEnCiel Link panel**, create/select a **Link Key (`lk_...`)**, then click **Connect**.
3. If your local endpoint is auto-detected, ArcEnCiel assigns it and toggles the worker.
4. If SwarmUI runs on a custom host/port, use **Find WebUIs** and assign the endpoint manually via **Custom...**.
5. Fallback/advanced path: edit settings in SwarmUI (`ArcEnCiel Link` server settings card) and enable the worker there.

---

## Credentials and security

- **Link Key (`lk_...`) is the primary credential** for the current ArcEnCiel worker websocket flow.
- API key fields remain for legacy/self-hosted compatibility, but Link Keys are recommended for active setups.
- Config path:

```text
Data/Extensions/ArcEnCielLink/config.json
```

Default schema:

```json
{
  "baseUrl": "https://link.arcenciel.io/api/link",
  "linkKey": "",
  "apiKey": "",
  "enabled": false,
  "minFreeMb": 2048,
  "maxRetries": 5,
  "backoffBase": 2,
  "saveHtmlPreview": false,
  "allowPrivateOrigins": false
}
```

Environment overrides:

- `ARCENCIEL_DEV=1` allows private origins and local HTTP during testing.

---

## How to use

- Press Download on any ArcEnCiel model card.
- Queue/progress state is visible in the ArcEnCiel Link panel and SwarmUI logs.
- Inventory sync runs periodically and duplicate downloads are skipped.

---

## Local API surface

- GET `/arcenciel-link/ping`
- POST `/arcenciel-link/toggle_link`
- GET `/arcenciel-link/folders/{kind}`
- POST `/arcenciel-link/generate_sidecars`

---

## Advanced configuration

- `minFreeMb`, `maxRetries`, and `backoffBase` live in config.json.
- `saveHtmlPreview` enables HTML quick-views next to model files.
- `allowPrivateOrigins` permits non-arcenciel origins for local testing.

---

## Troubleshooting

| Symptom                                            | Fix                                                                                                                                                                             |
| -------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Worker stays offline                               | Use a valid Link Key and click Connect from the ArcEnCiel Link panel.                                                                                                           |
| Connect only works after manual endpoint selection | ArcEnCiel auto-detect scans `127.0.0.1` and `localhost` on ports `7860`, `7861`, `7801`, `8000`, `8501`. For custom host/port, assign the endpoint manually with **Custom...**. |
| Browser reports private network blocked            | Accept the browser PNA prompt or enable `allowPrivateOrigins` for local testing.                                                                                                |
| Download stuck at 0%                               | Check disk space and write permissions.                                                                                                                                         |
| Repeated SHA256 mismatch                           | Usually network instability or a bad mirror.                                                                                                                                    |
| API key no longer connects worker                  | API keys are legacy; use a Link Key for current ArcEnCiel websocket auth.                                                                                                       |

---

## Development notes

- Namespace must not include `SwarmUI` for this to load as an external extension.
- Source layout:

```text
ArcEnCielLinkExtension.cs  # entrypoint (must stay at extension root)
src/                       # worker/runtime/cors/paths helpers
Assets/                    # optional static assets
```

---

## License

MIT
