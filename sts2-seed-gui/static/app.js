(() => {
  const $ = (id) => document.getElementById(id);
  let session = null;
  const pretty = (value) => JSON.stringify(value ?? {}, null, 2);
  const request = async (url, options = {}) => {
    const response = await fetch(url, { headers: { "Content-Type": "application/json" }, ...options });
    const body = await response.json();
    if (!response.ok) throw new Error(body.error || `HTTP ${response.status}`);
    return body;
  };
  const showError = (target, error) => { $(target).textContent = String(error.message || error); $(target).className = "output error"; };
  const showOk = (target, value) => { $(target).textContent = typeof value === "string" ? value : pretty(value); $(target).className = "output ok"; };

  async function health() {
    try { const value = await request("/api/health"); $("health").textContent = "后端已连接"; $("health").title = value.default_cli; }
    catch (error) { $("health").textContent = "后端不可用"; $("health").className = "pill error"; }
  }

  function contextPayload() {
    return {
      context: { run_seed: $("seed").value.trim(), character: $("character").value, ascension: Number($("ascension").value || 0), game_version: $("game-version").value.trim() },
      source: { type: "expert_video", id: $("source-id").value.trim() || "manual", url: $("source-url").value.trim(), expert_id: $("expert-id").value.trim(), sl_status: $("sl-status").value },
      cli_path: $("cli-path").value.trim() || undefined,
      start_cli: $("start-cli").checked,
    };
  }

  function renderMap(value) {
    const map = $("map"); map.innerHTML = "";
    const rows = value?.rows || [];
    if (!rows.length) { map.innerHTML = '<p class="muted">暂无地图。请启动 CLI 后刷新，或先导入 .run。</p>'; return; }
    rows.forEach((row) => {
      const el = document.createElement("div"); el.className = "map-row";
      row.forEach((node) => {
        const button = document.createElement("button"); button.className = `map-node${node.current ? " current" : ""}${node.visited ? " visited" : ""}`;
        button.innerHTML = `${node.type || "Unknown"}<small>(${node.col},${node.row})${node.current ? " · 当前" : ""}</small>`;
        button.onclick = () => { $("node-id").value = `map:${node.row}:${node.col}`; $("act").value = value?.context?.act || ""; $("floor").value = node.row || ""; };
        el.appendChild(button);
      }); map.appendChild(el);
    });
  }

  function renderSession(value) {
    session = value;
    $("workspace-card").classList.remove("hidden"); $("decision-card").classList.remove("hidden");
    $("context-output").textContent = pretty({ session_id: value.session_id, run_context_hash: value.run_context_hash, context: value.context, source: value.source, cli: value.cli });
    $("state").textContent = pretty(value.public_state);
    renderMap(value.map);
    const list = $("decisions"); list.innerHTML = "";
    (value.decisions || []).forEach((item) => { const el = document.createElement("div"); el.className = "decision-item"; el.innerHTML = `<strong>${item.decision_type}</strong> · ${item.selected_action?.action_id || ""} · ${item.video_timestamp || "无时间戳"}<br><span class="muted">${item.label_quality} / ${item.sl_status}</span>`; list.appendChild(el); });
    const checkpoints = value.checkpoints || [];
    $("restore").disabled = checkpoints.length === 0;
    $("restore").title = checkpoints.length ? `最近：${checkpoints[checkpoints.length - 1].label}` : "暂无 checkpoint";
  }

  $("create").onclick = async () => { try { renderSession(await request("/api/sessions", { method: "POST", body: JSON.stringify(contextPayload()) })); showOk("context-output", session); } catch (error) { showError("context-output", error); } };
  $("refresh-map").onclick = async () => { try { const value = await request(`/api/sessions/${session.session_id}/refresh-map`, { method: "POST", body: "{}" }); session.map = value; session.public_state = value; renderSession(session); } catch (error) { showError("state", error); } };
  $("checkpoint").onclick = async () => { try { showOk("context-output", await request(`/api/sessions/${session.session_id}/checkpoints`, { method: "POST", body: JSON.stringify({ label: `floor-${$("floor").value || "unknown"}` }) })); session = await request(`/api/sessions/${session.session_id}`); } catch (error) { showError("context-output", error); } };
  $("restore").onclick = async () => { try { const checkpoints = session.checkpoints || []; if (!checkpoints.length) return; const checkpoint = checkpoints[checkpoints.length - 1]; const value = await request(`/api/sessions/${session.session_id}/restore-checkpoint`, { method: "POST", body: JSON.stringify({ checkpoint_id: checkpoint.checkpoint_id }) }); renderSession(value.session); showOk("context-output", { restored: checkpoint.checkpoint_id }); } catch (error) { showError("context-output", error); } };
  $("branch").onclick = async () => { try { const checkpoint = await request(`/api/sessions/${session.session_id}/checkpoints`, { method: "POST", body: JSON.stringify({ label: "branch-parent" }) }); const branch = await request(`/api/sessions/${session.session_id}/branches`, { method: "POST", body: JSON.stringify({ name: "counterfactual", parent_checkpoint_id: checkpoint.checkpoint_id }) }); showOk("context-output", { checkpoint, branch }); session = await request(`/api/sessions/${session.session_id}`); } catch (error) { showError("context-output", error); } };
  $("import-run").onclick = async () => { try { const value = await request(`/api/sessions/${session.session_id}/import-run`, { method: "POST", body: JSON.stringify({ path: $("run-path").value.trim() }) }); $("run-summary").textContent = pretty({ run: value.run, floor_count: value.floors?.length, player: value.player, map_alignment: value.map_alignment }); showOk("run-summary", { run: value.run, floor_count: value.floors?.length, map_alignment: value.map_alignment }); session = await request(`/api/sessions/${session.session_id}`); } catch (error) { showError("run-summary", error); } };
  $("record").onclick = async () => {
    try {
      let legal = []; if ($("legal-actions").value.trim()) legal = JSON.parse($("legal-actions").value);
      let combatSummary = null; if ($("combat-summary").value.trim()) combatSummary = JSON.parse($("combat-summary").value);
      const confidence = $("confidence").value.trim();
      const payload = { decision_type: $("decision-type").value, act: Number($("act").value) || null, floor: Number($("floor").value) || null, node_id: $("node-id").value || null, next_node: $("next-node").value || null, video_timestamp: $("timestamp").value || null, confidence: confidence ? Number(confidence) : null, sl_status: $("sl-status").value, selected_action: { action_id: $("action-id").value.trim() }, legal_actions: legal, notes: $("notes").value, combat_summary: combatSummary, execute: $("execute").checked };
      const value = await request(`/api/sessions/${session.session_id}/decisions`, { method: "POST", body: JSON.stringify(payload) });
      const updated = await request(`/api/sessions/${session.session_id}`); renderSession(updated); showOk("decision-output", value);
    } catch (error) { showError("decision-output", error); }
  };
  $("export").onclick = async () => { try { showOk("decision-output", await request(`/api/sessions/${session.session_id}/export`, { method: "POST", body: "{}" })); } catch (error) { showError("decision-output", error); } };
  $("export-reliable").onclick = async () => { try { showOk("decision-output", await request(`/api/sessions/${session.session_id}/export-reliable`, { method: "POST", body: "{}" })); } catch (error) { showError("decision-output", error); } };
  $("validate").onclick = async () => { try { showOk("validation-output", await request(`/api/sessions/${session.session_id}/validate`, { method: "POST", body: "{}" })); } catch (error) { showError("validation-output", error); } };
  health();
})();
