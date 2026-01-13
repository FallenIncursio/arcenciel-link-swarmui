# ArcEnCiel Link - SwarmUI Extension

Bring your ArcEnCiel models straight into SwarmUI with one click. Includes
Link key support, remote worker control, inventory sync, and sidecar generation.

---

## Features

- One-click download from ArcEnCiel model cards.
- Model-aware routing for checkpoints, LoRAs, VAEs, and embeddings.
- Background worker with retry back-off, disk-space guard, and SHA-256 verify.
- Inventory sync so the dashboard knows what you already have.
- Optional preview PNG, `.arcenciel.info`, and HTML quick-view sidecars.

---

## Installation (SwarmUI)

1. Clone into:

```
SwarmUI/src/Extensions/ArcEnCielLink
```

2. Rebuild SwarmUI (`launch-dev` or run the `update` script).

Important: `ArcEnCielLinkExtension.cs` must stay at the extension root so SwarmUI
can detect and load the extension.

---

## First-time setup

1. On arcenciel.io open **Link Access** and create a Link Key (`lk_...`).
2. In SwarmUI settings (or ArcEnCiel web UI) paste the Link Key.
3. Enable the worker.

---

## Credentials and security

- Link Keys are the preferred credential.
- API keys are supported for legacy workflows, but Link Keys unlock remote worker
  controls and scope-based permissions.
- Config lives at:

```
Data/Extensions/ArcEnCielLink/config.json
```

Default schema:

```
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
- The job appears in the ArcEnCiel Link queue.
- Inventory sync runs periodically and the dashboard skips duplicates.

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

| Symptom | Fix |
|---------|-----|
| Worker stays offline | Ensure Link Key is saved and enable the worker. |
| Browser reports private network blocked | Accept the Private Network Access prompt or enable `allowPrivateOrigins`. |
| Download stuck at 0% | Check disk space and write permissions. |
| Repeated SHA256 mismatch | Network instability or corrupted mirror. |

---

## Development notes

- Namespace must not include `SwarmUI` for this to load as an external extension.
- Source layout:

```
ArcEnCielLinkExtension.cs  # entrypoint (must stay at extension root)
src/                       # worker/runtime/cors/paths helpers
Assets/                    # optional static assets
```

---

## License

MIT
