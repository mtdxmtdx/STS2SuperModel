(() => {
  const $ = id => document.getElementById(id);
  let session = null;
  let mapValue = null;
  let selectedNode = null;
  let catalogs = { cards: [], relics: [], potions: [] };

  const pretty = value => JSON.stringify(value ?? {}, null, 2);
  const nodeTypeZh = value => ({ monster: "战斗", combat: "战斗", elite: "精英", shop: "商店", rest: "篝火", restsite: "篝火", rest_site: "篝火", event: "事件", unknown: "问号", treasure: "宝箱", boss: "首领", ancient: "先古之民", map: "地图", start: "起点" }[String(value || "").toLowerCase()] || "节点");
  const decisionTypeZh = value => ({ route: "路线", neow: "涅奥", event: "事件", reward: "战斗奖励", shop: "商店", campfire: "篝火", combat: "战斗结果", resource_change: "资源变化" }[String(value || "").toLowerCase()] || "节点操作");
  const request = async (url, options = {}) => {
    const response = await fetch(url, { headers: { "Content-Type": "application/json" }, cache: "no-store", ...options });
    const body = await response.json();
    if (!response.ok) throw new Error(body.error || `HTTP ${response.status}`);
    return body;
  };
  const output = (id, value, error = false) => {
    $(id).textContent = typeof value === "string" ? value : pretty(value);
    $(id).className = `output ${error ? "error" : "ok"}`;
  };

  const fallback = {
    cards: [{ id: "STRIKE_IRONCLAD", name_zh: "打击" }, { id: "DEFEND_IRONCLAD", name_zh: "防御" }, { id: "BASH", name_zh: "痛击" }],
    relics: [{ id: "WINGED_BOOTS", name_zh: "羽翼之靴" }, { id: "BURNING_BLOOD", name_zh: "燃烧之血" }],
    potions: [{ id: "BLOCK_POTION", name_zh: "格挡药水" }, { id: "FIRE_POTION", name_zh: "火焰药水" }],
  };

  const normalizeItem = (value, kind) => {
    if (value && typeof value === "object") {
      const id = value.id || value.card_id || value.relic_id || value.potion_id || value.model_id;
      const name = value.name_zh || value.localized_name_zh || value.name || value.canonical_name || id;
      return id ? { id: String(id), name_zh: String(name || id) } : null;
    }
    if (typeof value === "string" && value.trim()) {
      const id = value.trim();
      const known = (catalogs[kind] || []).find(item => item.id === id || item.name_zh === id);
      return known || { id, name_zh: id };
    }
    return null;
  };
  const addUnique = (list, value, kind) => {
    const item = normalizeItem(value, kind);
    if (item && !list.some(existing => existing.id === item.id)) list.push(item);
  };
  const aliases = { cards: ["cards", "deck", "card_list", "cards_owned"], relics: ["relics", "relic_list", "relics_owned"], potions: ["potions", "potion_slots", "potion_list"] };

  function buildInventory(value) {
    const result = { cards: [], relics: [], potions: [] };
    const sources = [value?.public_state, value?.public_state?.player, value?.public_state?.player_state, value?.run_history?.player].filter(Boolean);
    for (const source of sources) {
      for (const kind of Object.keys(result)) {
        for (const key of aliases[kind]) {
          const entries = source[key];
          if (Array.isArray(entries)) entries.forEach(entry => addUnique(result[kind], entry, kind));
        }
      }
    }
    for (const record of value?.decisions || []) {
      const changes = record.realized_outcome || {};
      for (const kind of Object.keys(result)) {
        const group = changes[kind] || {};
        (group.gained || []).forEach(entry => addUnique(result[kind], entry, kind));
        (group.lost || []).forEach(entry => { const item = normalizeItem(entry, kind); if (item) result[kind] = result[kind].filter(existing => existing.id !== item.id); });
      }
    }
    return result;
  }

  function itemOptions(kind, mode) {
    const list = mode === "lost" ? buildInventory(session || {})[kind] : ((catalogs[kind] && catalogs[kind].length) ? catalogs[kind] : fallback[kind]);
    const seen = new Set();
    return list.filter(item => { if (seen.has(item.id)) return false; seen.add(item.id); return true; });
  }

  function ensureDatalist(kind, mode) {
    const id = `list-${kind}-${mode}`;
    let list = $(id);
    if (!list) { list = document.createElement("datalist"); list.id = id; document.body.appendChild(list); }
    list.innerHTML = itemOptions(kind, mode).map(item => `<option value="${escapeHtml(item.name_zh)}" label="${escapeHtml(item.name_zh)}"></option>`).join("");
    return id;
  }
  const escapeHtml = value => String(value ?? "").replace(/[&<>"']/g, ch => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[ch]));

  function renderInventory() {
    const value = buildInventory(session || {});
    $("inventory").innerHTML = Object.entries(value).map(([kind, entries]) => {
      const label = { cards: "卡牌", relics: "遗物", potions: "药水" }[kind];
      return `<div class="inventory-group"><strong>${label}</strong><span>${entries.length ? entries.map(item => escapeHtml(item.name_zh)).join("、") : "暂无已知记录"}</span></div>`;
    }).join("");
  }

  const nodeId = node => `map:${node.act || mapValue?.act || mapValue?.context?.act || 1}:${node.row}:${node.col}`;
  function flattenMap(value) {
    const rows = Array.isArray(value?.rows) ? value.rows : [];
    const result = [];
    rows.forEach(row => (Array.isArray(row) ? row : []).forEach(raw => {
      if (!raw || typeof raw !== "object") return;
      const node = { ...raw, act: raw.act || value.act || value.context?.act || 1 };
      node.node_id = nodeId(node);
      result.push(node);
    }));
    if (result.length) {
      const act = result[0].act;
      const firstRow = result.reduce((min, node) => Math.min(min, Number(node.row)), Number(result[0].row));
      const firstNodes = result.filter(node => Number(node.row) === firstRow);
      if (!result.some(node => String(node.type).toLowerCase() === "ancient")) {
        const currentCoord = value?.current_coord;
        const ancientRow = currentCoord && Number(currentCoord.row) < firstRow ? Number(currentCoord.row) : firstRow - 1;
        const ancientCol = currentCoord && ancientRow < firstRow ? Number(currentCoord.col) : Math.round(firstNodes.reduce((sum, node) => sum + Number(node.col || 0), 0) / Math.max(1, firstNodes.length));
        const ancient = { act, row: ancientRow, col: ancientCol, type: "Ancient", synthetic_ancient: true, children: firstNodes.map(node => ({ row: node.row, col: node.col })), visited: false, current: Boolean(currentCoord && Number(currentCoord.row) === ancientRow && Number(currentCoord.col) === ancientCol) };
        ancient.node_id = nodeId(ancient); result.push(ancient);
      }
      if (value?.boss && !result.some(node => String(node.type).toLowerCase() === "boss")) {
        const boss = { ...value.boss, act, type: "Boss", children: [], synthetic_boss: true }; boss.node_id = nodeId(boss);
        const lastRow = result.filter(node => !node.synthetic_ancient).reduce((max, node) => Math.max(max, Number(node.row)), 0);
        result.filter(node => Number(node.row) === lastRow).forEach(node => { node.children = [...(node.children || []), { row: boss.row, col: boss.col }]; });
        result.push(boss);
      }
    }
    return result;
  }

  function highlightNode(node) {
    selectedNode = node;
    document.querySelectorAll(".map-node-svg").forEach(element => element.classList.toggle("selected", element.dataset.nodeId === node.node_id));
    $("selected-node-label").textContent = `已高亮：${nodeTypeZh(node.type)}，第 ${node.row} 层，坐标 (${node.col},${node.row})`;
  }

  function renderMap(value) {
    mapValue = value;
    const host = $("map");
    host.innerHTML = "";
    const nodes = flattenMap(value);
    if (!nodes.length) { host.innerHTML = '<p class="muted empty-map">暂无地图，请确认 CLI 已启动。</p>'; return; }
    const byId = new Map(nodes.map(node => [node.node_id, node]));
    const maxCol = nodes.reduce((max, node) => Math.max(max, Number(node.col || 0)), 0);
    const maxRow = nodes.reduce((max, node) => Math.max(max, Number(node.row || 0)), 0);
    const width = Math.max(820, maxCol * 118 + 180);
    const height = Math.max(620, maxRow * 78 + 120);
    const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    svg.setAttribute("viewBox", `0 0 ${width} ${height}`); svg.classList.add("map-canvas");
    nodes.forEach(node => (node.children || []).forEach(childRef => {
      const child = typeof childRef === "string" ? byId.get(childRef) : byId.get(`map:${node.act}:${childRef.row}:${childRef.col}`);
      if (!child) return;
      const line = document.createElementNS("http://www.w3.org/2000/svg", "line");
      line.setAttribute("x1", Number(node.col || 0) * 118 + 70); line.setAttribute("y1", height - Number(node.row || 0) * 78 - 58);
      line.setAttribute("x2", Number(child.col || 0) * 118 + 70); line.setAttribute("y2", height - Number(child.row || 0) * 78 - 58); line.classList.add("map-edge"); svg.appendChild(line);
    }));
    nodes.forEach(node => {
      const group = document.createElementNS("http://www.w3.org/2000/svg", "g");
      group.dataset.nodeId = node.node_id; group.classList.add("map-node-svg");
      const routeNodes = new Set(session?.route_state?.selected || []);
      if (node.current) group.classList.add("current"); if (node.visited) group.classList.add("visited"); if (routeNodes.has(node.node_id)) group.classList.add("route-selected"); if (node.synthetic_ancient) group.classList.add("ancient"); if (selectedNode?.node_id === node.node_id) group.classList.add("selected");
      const x = Number(node.col || 0) * 118 + 70; const y = height - Number(node.row || 0) * 78 - 58;
      const circle = document.createElementNS("http://www.w3.org/2000/svg", "circle"); circle.setAttribute("cx", x); circle.setAttribute("cy", y); circle.setAttribute("r", 24); group.appendChild(circle);
      const text = document.createElementNS("http://www.w3.org/2000/svg", "text"); text.setAttribute("x", x); text.setAttribute("y", y + 4); text.setAttribute("text-anchor", "middle"); text.textContent = nodeTypeZh(node.type).slice(0, 4); group.appendChild(text);
      const title = document.createElementNS("http://www.w3.org/2000/svg", "title"); title.textContent = `${nodeTypeZh(node.type)} · 第 ${node.row} 层 · (${node.col},${node.row})`; group.appendChild(title);
      group.addEventListener("click", () => highlightNode(node));
      group.addEventListener("contextmenu", event => { event.preventDefault(); highlightNode(node); openNodePopup(node, event); });
      svg.appendChild(group);
    });
    host.appendChild(svg);
  }

  function addItemRow(container, kind, mode, value = "") {
    const listId = ensureDatalist(kind, mode);
    const row = document.createElement("div"); row.className = "item-row"; row.dataset.kind = kind; row.dataset.mode = mode;
    row.innerHTML = `<input class="item-input" list="${listId}" value="${escapeHtml(value)}" placeholder="搜索或输入${mode === "lost" ? "已拥有的" : "要获得的"}${{ cards: "卡牌", relics: "遗物", potions: "药水" }[kind]}"><button type="button" class="remove-item" title="删除这一行">−</button>`;
    row.querySelector(".remove-item").onclick = () => row.remove(); container.appendChild(row);
  }

  function itemSection(kind, mode, label) {
    const id = `items-${kind}-${mode}`;
    return `<div class="item-section"><div class="item-section-head"><strong>${label}</strong><button type="button" class="add-item" data-container="${id}" data-kind="${kind}" data-mode="${mode}">＋ 添加</button></div><div id="${id}" class="item-rows"></div></div>`;
  }

  function openNodePopup(node, event) {
    const popup = $("node-popup");
    popup.innerHTML = `<div class="popup-head"><div><strong>${escapeHtml(nodeTypeZh(node.type))}</strong><span>第 ${node.row} 层 · 坐标 (${node.col},${node.row})</span></div><button type="button" id="close-popup" class="close-popup">×</button></div>
      ${node.synthetic_ancient ? "" : '<button type="button" id="choose-route" class="route-button">选择为路线节点</button>'}
      <div class="resource-grid"><label>生命变化<input id="popup-hp" type="number" step="1" placeholder="减少填负数，增加填正数"></label><label>金币变化<input id="popup-gold" type="number" step="1" placeholder="减少填负数，增加填正数"></label></div>
      ${itemSection("cards", "lost", "失去的卡牌")}${itemSection("cards", "gained", "获得的卡牌")}
      ${itemSection("relics", "lost", "失去的遗物")}${itemSection("relics", "gained", "获得的遗物")}
      ${itemSection("potions", "lost", "失去的药水")}${itemSection("potions", "gained", "获得的药水")}
      <label>本节点操作<select id="popup-operation"><option value="none">仅记录资源变化</option><option value="neow">涅奥选择</option><option value="event">事件选择</option><option value="reward">战斗奖励</option><option value="shop">商店操作</option><option value="campfire">篝火操作</option><option value="combat">战斗结果摘要</option></select></label>
      <label>备注<textarea id="popup-notes" rows="2" placeholder="例如：用羽翼之靴跳到下一层精英"></textarea></label>
      <button type="button" id="save-popup" class="save-button">保存这个节点的数据</button>`;
    popup.classList.remove("hidden");
    const stageRect = $("map-stage").getBoundingClientRect();
    const maxLeft = Math.max(8, stageRect.width - 370); const maxTop = Math.max(8, stageRect.height - 580);
    popup.style.left = `${Math.max(8, Math.min(maxLeft, event.clientX - stageRect.left + 14))}px`;
    popup.style.top = `${Math.max(8, Math.min(maxTop, event.clientY - stageRect.top + 14))}px`;
    $("close-popup").onclick = () => popup.classList.add("hidden");
    if ($("choose-route")) $("choose-route").onclick = () => chooseRoute(node);
    if (node.synthetic_ancient) $("popup-operation").value = "neow";
    document.querySelectorAll(".add-item").forEach(button => { button.onclick = () => addItemRow($(button.dataset.container), button.dataset.kind, button.dataset.mode); });
    ["cards", "relics", "potions"].forEach(kind => { addItemRow($(`items-${kind}-lost`), kind, "lost"); addItemRow($(`items-${kind}-gained`), kind, "gained"); });
    $("save-popup").onclick = () => saveNodeData(node);
  }

  function readItems(kind, mode) {
    const known = itemOptions(kind, mode);
    return [...document.querySelectorAll(`.item-row[data-kind="${kind}"][data-mode="${mode}"] .item-input`)].map(input => known.find(item => item.name_zh === input.value) || normalizeItem(input.value, kind)).filter(Boolean);
  }
  async function chooseRoute(node) {
    if (!session) return;
    try {
      const result = await request(`/api/sessions/${session.session_id}/route-select`, { method: "POST", body: JSON.stringify({ node_id: node.node_id }) });
      if (!result.legal) throw new Error(`路线不可达：${result.code}`);
      await request(`/api/sessions/${session.session_id}/decisions`, { method: "POST", body: JSON.stringify({ decision_type: "route", act: node.act, floor: node.row, node_id: node.node_id, node_coord: { row: node.row, col: node.col }, node_type: node.type, selected_action: { action_id: `route:${node.node_id}` }, next_node: node.node_id, sl_status: "unknown", notes: "地图左键高亮后选择路线" }) });
      session = await request(`/api/sessions/${session.session_id}`); selectedNode = null; renderSession(session); $("selected-node-label").textContent = `路线节点已保存：第 ${node.row} 层，坐标 (${node.col},${node.row})`; $("node-popup").classList.add("hidden");
    } catch (error) { output("decision-output", error.message, true); }
  }
  async function saveNodeData(node) {
    try {
      const outcome = { operation: $("popup-operation").value, hp_delta: Number($("popup-hp").value || 0), gold_delta: Number($("popup-gold").value || 0), cards: { lost: readItems("cards", "lost"), gained: readItems("cards", "gained") }, relics: { lost: readItems("relics", "lost"), gained: readItems("relics", "gained") }, potions: { lost: readItems("potions", "lost"), gained: readItems("potions", "gained") } };
      const actionName = $("popup-operation").value === "none" ? "resource_change" : $("popup-operation").value;
      await request(`/api/sessions/${session.session_id}/operations`, { method: "POST", body: JSON.stringify({ decision_type: actionName, act: node.act, floor: node.row, node_id: node.node_id, node_coord: { row: node.row, col: node.col }, node_type: node.type, selected_action: { action_id: `${actionName}:${node.node_id}` }, realized_outcome: outcome, combat_summary: actionName === "combat" ? outcome : null, sl_status: "unknown", notes: $("popup-notes").value }) });
      session = await request(`/api/sessions/${session.session_id}`); renderSession(session); $("node-popup").classList.add("hidden"); output("decision-output", { saved: true, node_id: node.node_id });
    } catch (error) { output("decision-output", error.message, true); }
  }

  function renderSession(value) {
    session = value; $("workspace-card").classList.remove("hidden"); $("records-card").classList.remove("hidden");
    output("context-output", { session_id: value.session_id, run_context_hash: value.run_context_hash, cli: value.cli }); $("state").textContent = pretty(value.public_state); renderMap(value.map || value); renderInventory();
    const route = value.route_state || {}; $("route-status").textContent = `已记录 ${route.selected?.length || 0} 个节点 · 羽翼之靴剩余 ${route.winged_boots_charges || 0} 次`;
    const list = $("decisions"); list.innerHTML = ""; (value.decisions || []).forEach(item => { const element = document.createElement("div"); element.className = "decision-item"; element.innerHTML = `<strong>${escapeHtml(decisionTypeZh(item.decision_type))}</strong><span>${escapeHtml(item.node_id || "")}</span><small>${escapeHtml(item.notes || "")}</small>`; list.appendChild(element); });
  }

  $("create").onclick = async () => { try { const value = await request("/api/sessions", { method: "POST", body: JSON.stringify({ context: { run_seed: $("seed").value.trim(), character: $("character").value, ascension: Number($("ascension").value || 0), game_version: $("game-version").value.trim() }, source: { type: "manual_annotation", id: $("source-id").value.trim() || "manual", annotator_id: $("annotator-id").value.trim(), sl_status: "unknown" }, cli_path: $("cli-path").value.trim() || undefined, start_cli: true }) }); renderSession(value); } catch (error) { output("context-output", error.message, true); } };
  $("refresh-map").onclick = async () => { try { const map = await request(`/api/sessions/${session.session_id}/refresh-map`, { method: "POST", body: "{}" }); session.map = map; renderSession(session); } catch (error) { output("state", error.message, true); } };
  $("checkpoint").onclick = async () => { try { output("context-output", await request(`/api/sessions/${session.session_id}/checkpoints`, { method: "POST", body: JSON.stringify({ label: "手动检查点" }) })); } catch (error) { output("context-output", error.message, true); } };
  $("restore").onclick = async () => { try { const checkpoint = (session.checkpoints || []).at(-1); if (!checkpoint) return; renderSession((await request(`/api/sessions/${session.session_id}/restore-checkpoint`, { method: "POST", body: JSON.stringify({ checkpoint_id: checkpoint.checkpoint_id }) })).session); } catch (error) { output("context-output", error.message, true); } };
  $("validate").onclick = async () => { try { output("validation-output", await request(`/api/sessions/${session.session_id}/validate`, { method: "POST", body: "{}" })); } catch (error) { output("validation-output", error.message, true); } };
  $("export").onclick = async () => { try { output("decision-output", await request(`/api/sessions/${session.session_id}/export`, { method: "POST", body: "{}" })); } catch (error) { output("decision-output", error.message, true); } };
  $("import-run").onclick = async () => { try { const value = await request(`/api/sessions/${session.session_id}/import-run`, { method: "POST", body: JSON.stringify({ path: $("run-path").value.trim() }) }); output("run-summary", { run: value.run, floor_count: value.floors?.length, map_alignment: value.map_alignment }); session = await request(`/api/sessions/${session.session_id}`); renderSession(session); } catch (error) { output("run-summary", error.message, true); } };

  request("/api/catalogs").then(value => { catalogs = { cards: value.cards || fallback.cards, relics: value.relics || fallback.relics, potions: value.potions || fallback.potions }; }).catch(() => { catalogs = fallback; });
  const health = async () => { try { const value = await request("/api/health"); $("health").textContent = "后端已连接"; $("health").className = "pill"; $("health").title = value.default_cli || ""; } catch (error) { $("health").textContent = "后端不可用"; $("health").className = "pill error"; $("health").title = String(error.message || error); } };
  health();
})();
