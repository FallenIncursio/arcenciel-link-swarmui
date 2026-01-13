class ArcEnCielLinkSettingsUI {
    constructor() {
        this.state = null;
    }

    register() {
        if (document.getElementById('arcenciel_link_settings_card')) {
            return;
        }

        const confirmer = document.getElementById('serversettings_confirmer');
        if (!confirmer) {
            return;
        }

        const card = document.createElement('div');
        card.className = 'card border-secondary mb-3';
        card.id = 'arcenciel_link_settings_card';
        card.innerHTML = `
            <div class="card-header translate">ArcEnCiel Link</div>
            <div class="card-body">
                <p class="card-text translate">Configure the ArcEnCiel Link worker that syncs models to your ArcEnCiel library.</p>
                <div id="arcenciel_link_settings_inputs" class="settings-container"></div>
                <div style="margin-top: 0.5rem;">
                    <button class="basic-button translate" id="arcenciel_link_settings_save">Save</button>
                    <button class="basic-button translate" id="arcenciel_link_settings_reload">Reload</button>
                    <span id="arcenciel_link_settings_status" style="margin-left: 0.75rem;"></span>
                </div>
                <div id="arcenciel_link_settings_meta" class="translate" style="margin-top: 0.5rem;"></div>
            </div>`;
        confirmer.parentNode.insertBefore(card, confirmer);

        const inputs = getRequiredElementById('arcenciel_link_settings_inputs');
        inputs.innerHTML =
            makeTextInput(null, 'arcenciel_link_base_url', '', 'Base URL', 'ArcEnCiel Link API endpoint.', '', 'normal', 'https://link.arcenciel.io/api/link', false, true)
            + makeTextInput(null, 'arcenciel_link_link_key', '', 'Link Key', 'Link key used for verification (starts with lk_).', '', 'secret', 'lk_...', false, true)
            + makeTextInput(null, 'arcenciel_link_api_key', '', 'API Key', 'Optional API key if no Link Key is provided.', '', 'secret', 'optional', false, true)
            + makeCheckboxInput(null, 'arcenciel_link_enabled', '', 'Enable Link Worker', 'Enable the ArcEnCiel Link worker connection.', false, false, true)
            + makeNumberInput(null, 'arcenciel_link_min_free_mb', '', 'Min Free Disk (MB)', 'Minimum free disk space required to accept downloads.', 2048, 0, 1000000, 1, 'normal', false, true)
            + makeNumberInput(null, 'arcenciel_link_max_retries', '', 'Max Retries', 'Maximum download retries per job.', 5, 1, 20, 1, 'normal', false, true)
            + makeNumberInput(null, 'arcenciel_link_backoff_base', '', 'Backoff Base', 'Retry backoff base (exponential).', 2, 1, 10, 1, 'normal', false, true)
            + makeCheckboxInput(null, 'arcenciel_link_save_html', '', 'Save HTML Previews', 'Save HTML preview sidecars when available.', false, false, true)
            + makeCheckboxInput(null, 'arcenciel_link_allow_private', '', 'Allow Private Origins', 'Allow private/localhost origins for local development.', false, false, true);

        getRequiredElementById('arcenciel_link_settings_save').addEventListener('click', () => this.save());
        getRequiredElementById('arcenciel_link_settings_reload').addEventListener('click', () => this.load());

        const serverConfigTab = document.getElementById('serverconfigtabbutton');
        if (serverConfigTab) {
            serverConfigTab.addEventListener('click', () => this.load());
        }

        this.load();
    }

    setStatus(message, isError = false) {
        const status = getRequiredElementById('arcenciel_link_settings_status');
        status.textContent = message || '';
        status.classList.remove('text-danger', 'text-success');
        if (message) {
            status.classList.add(isError ? 'text-danger' : 'text-success');
        }
    }

    setMeta(message) {
        getRequiredElementById('arcenciel_link_settings_meta').textContent = message || '';
    }

    applyState(data) {
        this.state = data;

        getRequiredElementById('arcenciel_link_base_url').value = data.baseUrl || '';
        getRequiredElementById('arcenciel_link_enabled').checked = !!data.enabled;
        getRequiredElementById('arcenciel_link_min_free_mb').value = data.minFreeMb ?? 2048;
        getRequiredElementById('arcenciel_link_max_retries').value = data.maxRetries ?? 5;
        getRequiredElementById('arcenciel_link_backoff_base').value = data.backoffBase ?? 2;
        getRequiredElementById('arcenciel_link_save_html').checked = !!data.saveHtmlPreview;
        getRequiredElementById('arcenciel_link_allow_private').checked = !!data.allowPrivateOrigins;

        const linkKeyInput = getRequiredElementById('arcenciel_link_link_key');
        linkKeyInput.value = '';
        linkKeyInput.placeholder = data.linkKeySet ? 'Configured (leave blank to keep)' : 'lk_...';

        const apiKeyInput = getRequiredElementById('arcenciel_link_api_key');
        apiKeyInput.value = '';
        apiKeyInput.placeholder = data.apiKeySet ? 'Configured (leave blank to keep)' : 'optional';

        const parts = [];
        parts.push(`Worker: ${data.enabled ? 'Enabled' : 'Disabled'}`);
        parts.push(`Link Key: ${data.linkKeySet ? 'Set' : 'Missing'}`);
        parts.push(`API Key: ${data.apiKeySet ? 'Set' : 'Empty'}`);
        this.setMeta(parts.join(' | '));
    }

    async load() {
        this.setStatus('Loading...', false);
        try {
            const response = await fetch('/arcenciel-link/settings');
            if (!response.ok) {
                this.setStatus('Failed to load settings.', true);
                return;
            }
            const data = await response.json();
            if (data.error) {
                this.setStatus(data.error, true);
                return;
            }
            this.applyState(data);
            this.setStatus('Settings loaded.', false);
        }
        catch (err) {
            console.error(err);
            this.setStatus('Failed to load settings.', true);
        }
    }

    async save() {
        if (!this.state) {
            this.setStatus('Settings not loaded yet.', true);
            return;
        }

        const baseUrl = getRequiredElementById('arcenciel_link_base_url').value.trim();
        if (!baseUrl) {
            this.setStatus('Base URL is required.', true);
            return;
        }

        const enabled = getRequiredElementById('arcenciel_link_enabled').checked;
        const linkKey = getRequiredElementById('arcenciel_link_link_key').value.trim();
        const apiKey = getRequiredElementById('arcenciel_link_api_key').value.trim();

        if (enabled && !linkKey && !apiKey && !this.state.linkKeySet && !this.state.apiKeySet) {
            this.setStatus('Link Key or API Key required to enable.', true);
            return;
        }

        const minFreeMb = parseInt(getRequiredElementById('arcenciel_link_min_free_mb').value, 10);
        const maxRetries = parseInt(getRequiredElementById('arcenciel_link_max_retries').value, 10);
        const backoffBase = parseInt(getRequiredElementById('arcenciel_link_backoff_base').value, 10);

        if (Number.isNaN(minFreeMb) || minFreeMb < 0) {
            this.setStatus('Min Free Disk (MB) must be >= 0.', true);
            return;
        }
        if (Number.isNaN(maxRetries) || maxRetries < 1) {
            this.setStatus('Max Retries must be >= 1.', true);
            return;
        }
        if (Number.isNaN(backoffBase) || backoffBase < 1) {
            this.setStatus('Backoff Base must be >= 1.', true);
            return;
        }

        const payload = {
            baseUrl: baseUrl,
            enabled: enabled,
            minFreeMb: minFreeMb,
            maxRetries: maxRetries,
            backoffBase: backoffBase,
            saveHtmlPreview: getRequiredElementById('arcenciel_link_save_html').checked,
            allowPrivateOrigins: getRequiredElementById('arcenciel_link_allow_private').checked
        };

        if (linkKey) {
            payload.linkKey = linkKey;
        }
        if (apiKey) {
            payload.apiKey = apiKey;
        }

        this.setStatus('Saving...', false);
        try {
            const response = await fetch('/arcenciel-link/settings', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(payload)
            });
            const data = await response.json();
            if (!response.ok || data.error) {
                this.setStatus(data.error || 'Failed to save settings.', true);
                return;
            }
            this.setStatus('Settings saved.', false);
            await this.load();
        }
        catch (err) {
            console.error(err);
            this.setStatus('Failed to save settings.', true);
        }
    }
}

const arcencielLinkSettingsUI = new ArcEnCielLinkSettingsUI();
sessionReadyCallbacks.push(() => arcencielLinkSettingsUI.register());
