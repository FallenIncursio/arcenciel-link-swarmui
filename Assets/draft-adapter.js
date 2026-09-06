(() => {
  const ids = {
    prompt: "prompt",
    negativePrompt: "negativeprompt",
    seed: "seed",
    steps: "steps",
    cfg: "cfgscale",
    width: "width",
    height: "height",
    sampler: "sampler",
    scheduler: "scheduler",
  };
  const input = (key) => document.getElementById(`input_${ids[key]}`);
  const param = (key) => gen_param_types.find((p) => p.id === ids[key]);
  async function read(keys, requireEnabled = true) {
    return Object.fromEntries(
      keys.map((key) => {
        const e = input(key);
        if (!e) throw new Error(`This Swarm backend does not expose ${key}.`);
        const toggle = document.getElementById(`input_${ids[key]}_toggle`);
        if (requireEnabled && toggle && !toggle.checked)
          throw new Error(`The ${key} setting is still disabled.`);
        return [
          key,
          ["steps", "cfg", "width", "height"].includes(key)
            ? Number(e.value)
            : e.value,
        ];
      }),
    );
  }
  async function apply(fields, options = {}) {
    requireManualGeneration();
    for (const [key, value] of Object.entries(fields)) {
      const type = param(key),
        e = input(key);
      if (!type || !e)
        throw new Error(`The ${key} setting changed. Refresh your backend.`);
      setDirectParamValue(type, value, e, false, true);
      const toggle = document.getElementById(`input_${ids[key]}_toggle`);
      if (toggle && options.enable !== false) {
        toggle.checked = true;
        doToggleEnable(`input_${ids[key]}`);
        triggerChangeFor(toggle);
      }
    }
  }
  function requireManualGeneration() {
    if (
      (typeof isGeneratingForever !== "undefined" && isGeneratingForever) ||
      (typeof isGeneratingPreviews !== "undefined" && isGeneratingPreviews)
    )
      throw new Error(
        "Stop Generate Forever and Generate Previews before importing or restoring a Link draft. Your settings have not been changed.",
      );
  }
  const adapter = {
    read,
    apply,
    async snapshot() {
      const keys = Object.keys(ids).filter((key) => input(key));
      return {
        fields: await read(keys, false),
        toggles: Object.fromEntries(
          keys
            .filter((key) =>
              document.getElementById(`input_${ids[key]}_toggle`),
            )
            .map((key) => [
              key,
              document.getElementById(`input_${ids[key]}_toggle`).checked,
            ]),
        ),
      };
    },
    async restore(snapshot) {
      await apply(snapshot.fields, { enable: false });
      for (const [key, checked] of Object.entries(snapshot.toggles)) {
        const toggle = document.getElementById(`input_${ids[key]}_toggle`);
        if (!toggle)
          throw new Error(
            "The native settings changed; export the backup before restoring.",
          );
        toggle.checked = checked;
        doToggleEnable(`input_${ids[key]}`);
        triggerChangeFor(toggle);
      }
    },
    async check(fields) {
      requireManualGeneration();
      const checks = {};
      for (const [key, value] of Object.entries(fields)) {
        const e = input(key),
          type = param(key);
        let reason =
          !e || !type
            ? "The active backend does not expose this field."
            : undefined;
        if (
          type &&
          ["integer", "decimal"].includes(type.type) &&
          (Number(value) < type.min || Number(value) > type.max)
        )
          reason = `Outside the current range (${type.min} to ${type.max}).`;
        if (
          e?.tagName === "SELECT" &&
          ![...e.options].some((o) => o.value === String(value))
        )
          reason = "The active backend does not offer this option.";
        const toggle = document.getElementById(`input_${ids[key]}_toggle`);
        checks[key] = {
          before:
            toggle && !toggle.checked
              ? `Disabled (${e?.value}) — importing enables this setting`
              : e?.value,
          reason,
        };
      }
      return checks;
    },
  };
  sessionReadyCallbacks.push(() => {
    if (document.getElementById("aec-link-inbox-tab")) return;
    const tabs = document.getElementById("toptablist"),
      content = document.getElementById("Text2Image")?.parentElement;
    if (!tabs || !content || !window.AECLinkDrafts) return;
    // Keep native navigation above Swarm's transparent desktop status overlay.
    tabs.classList.add("aec-link-native-tabs");
    const style = document.createElement("style");
    style.textContent =
      "#toptablist.aec-link-native-tabs { position: relative; z-index: 1; }";
    document.head.append(style);
    const item = document.createElement("li");
    item.className = "nav-item";
    item.setAttribute("role", "presentation");
    const link = document.createElement("a");
    link.className = "nav-link";
    link.dataset.bsToggle = "tab";
    link.href = "#aec-link-inbox";
    link.id = "aec-link-inbox-tab";
    link.setAttribute("role", "tab");
    link.textContent = "Link inbox";
    item.append(link);
    tabs.insertBefore(
      item,
      document.getElementById("text2imagetabbutton")?.closest("li")
        ?.nextElementSibling || tabs.querySelector(".ms-auto"),
    );
    const panel = document.createElement("div");
    panel.id = "aec-link-inbox";
    panel.className = "tab-pane tab-pane-vw";
    panel.setAttribute("role", "tabpanel");
    panel.setAttribute("aria-labelledby", link.id);
    panel.style.padding = "20px";
    content.append(panel);
    window.AECLinkDrafts.mount(panel, adapter);
  });
})();
