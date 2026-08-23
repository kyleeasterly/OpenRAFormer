// orf browser-side observer renderer.
//
// Draws the match from baked assets (terrain PNG, engine-packed sprite atlases,
// manifest.json) plus a compact ~8 Hz actor feed — no video anywhere. Player
// colors are applied client-side: the atlas ships with the palette's remap
// pixels transparent, and a parallel mask atlas carries each remap pixel's ramp
// position as a gray value; we composite one tinted atlas per player color on
// an offscreen canvas at load time (the classic technique — no shaders needed).

const ASSETS = "webassets";

const err = m => { const e = document.getElementById("err"); e.style.display = "block"; e.textContent = m; };

const state = {
  manifest: null, map: null,
  sheets: {},          // sheetKey -> { base: HTMLImageElement, mask: HTMLImageElement|null }
  tinted: new Map(),   // `${sheetKey}/${ownerId}` -> PIXI.BaseTexture
  neutral: new Map(),  // sheetKey -> PIXI.BaseTexture
  textures: new Map(), // `${spriteId}/${ownerKey}` -> PIXI.Texture
  colors: {},          // slug -> "#rrggbb"
  slugOrder: [],       // ownerId -> slug (from actor feed)
  actors: new Map(),   // id -> { sprite, turret, bar, cur:{x,y}, from:{x,y}, to:{x,y}, t0, t1, typeName, hp }
  frameInterval: 125,
  names: {}, dead: new Set(),
};

// --- Pixi setup with drag-pan / wheel-zoom ----------------------------------
const app = new PIXI.Application({ resizeTo: window, background: 0x0b0b0d, antialias: false });
document.body.appendChild(app.view);
PIXI.BaseTexture.defaultOptions.scaleMode = PIXI.SCALE_MODES.NEAREST;

const world = new PIXI.Container();
app.stage.addChild(world);
const terrainLayer = new PIXI.Container();
const groundLayer = new PIXI.Container();   // resources, decorative map actors
const actorLayer = new PIXI.Container();
actorLayer.sortableChildren = true;
const fxLayer = new PIXI.Container();       // hp bars
world.addChild(terrainLayer, groundLayer, actorLayer, fxLayer);

let dragging = false, lastX = 0, lastY = 0;
app.view.addEventListener("mousedown", e => { dragging = true; lastX = e.clientX; lastY = e.clientY; });
window.addEventListener("mouseup", () => dragging = false);
window.addEventListener("mousemove", e => {
  if (!dragging) return;
  world.x += e.clientX - lastX;
  world.y += e.clientY - lastY;
  lastX = e.clientX; lastY = e.clientY;
});
app.view.addEventListener("wheel", e => {
  e.preventDefault();
  const factor = e.deltaY < 0 ? 1.15 : 1 / 1.15;
  const scale = Math.min(6, Math.max(0.15, world.scale.x * factor));
  // zoom around the cursor
  const wx = (e.clientX - world.x) / world.scale.x;
  const wy = (e.clientY - world.y) / world.scale.y;
  world.scale.set(scale);
  world.x = e.clientX - wx * scale;
  world.y = e.clientY - wy * scale;
}, { passive: false });

// --- Asset loading -----------------------------------------------------------
const loadImage = src => new Promise((res, rej) => {
  const img = new Image();
  img.onload = () => res(img);
  img.onerror = () => rej(new Error("failed to load " + src));
  img.src = src;
});

async function boot() {
  try {
    [state.manifest, state.map] = await Promise.all([
      (await fetch(`${ASSETS}/manifest.json`)).json(),
      (await fetch(`${ASSETS}/map.json`)).json(),
    ]);
  } catch (e) {
    err("Baked web assets not found — run: ./utility.sh cnc --llm-export-web-assets webassets <map>");
    return;
  }

  const sheetJobs = Object.entries(state.manifest.sheets).map(async ([key, files]) => {
    const base = await loadImage(`${ASSETS}/${files.file}`);
    const mask = files.mask ? await loadImage(`${ASSETS}/${files.mask}`) : null;
    state.sheets[key] = { base, mask };
  });
  const terrainImg = loadImage(`${ASSETS}/terrain.png`);
  await Promise.all([...sheetJobs, terrainImg]);

  terrainLayer.addChild(PIXI.Sprite.from(await terrainImg));

  // Fit + center the playable bounds initially.
  const b = state.map.bounds, ts = state.map.tileSize;
  const fit = Math.min(window.innerWidth / (b.w * ts), window.innerHeight / (b.h * ts));
  world.scale.set(fit);
  world.x = -b.x * ts * fit + (window.innerWidth - b.w * ts * fit) / 2;
  world.y = -b.y * ts * fit + (window.innerHeight - b.h * ts * fit) / 2;

  drawMapDecor();
  connect();
  app.ticker.add(tick);
}

// --- Tinting -----------------------------------------------------------------
function neutralBase(sheetKey) {
  if (!state.neutral.has(sheetKey))
    state.neutral.set(sheetKey, PIXI.BaseTexture.from(state.sheets[sheetKey].base));
  return state.neutral.get(sheetKey);
}

function tintedBase(sheetKey, ownerId) {
  const cacheKey = `${sheetKey}/${ownerId}`;
  if (state.tinted.has(cacheKey))
    return state.tinted.get(cacheKey);

  const { base, mask } = state.sheets[sheetKey];
  if (!mask) return neutralBase(sheetKey);

  const hex = state.colors[state.slugOrder[ownerId]] || "#888888";
  const cr = parseInt(hex.slice(1, 3), 16), cg = parseInt(hex.slice(3, 5), 16), cb = parseInt(hex.slice(5, 7), 16);

  const canvas = document.createElement("canvas");
  canvas.width = base.width; canvas.height = base.height;
  const ctx = canvas.getContext("2d");
  ctx.drawImage(base, 0, 0);
  const out = ctx.getImageData(0, 0, canvas.width, canvas.height);

  const mctx = document.createElement("canvas").getContext("2d");
  mctx.canvas.width = mask.width; mctx.canvas.height = mask.height;
  mctx.drawImage(mask, 0, 0);
  const m = mctx.getImageData(0, 0, mask.width, mask.height).data;

  for (let i = 0; i < m.length; i += 4) {
    if (m[i + 3] === 0) continue;
    // gray 0 = lightest ramp entry, 255 = darkest
    const L = 1.25 - 1.05 * (m[i] / 255);
    out.data[i] = Math.min(255, cr * L);
    out.data[i + 1] = Math.min(255, cg * L);
    out.data[i + 2] = Math.min(255, cb * L);
    out.data[i + 3] = 255;
  }

  ctx.putImageData(out, 0, 0);
  const tex = PIXI.BaseTexture.from(canvas);
  state.tinted.set(cacheKey, tex);
  return tex;
}

// ownerId -1 = neutral (map decor)
function texture(spriteId, ownerId) {
  if (spriteId < 0) return null;
  const key = `${spriteId}/${ownerId}`;
  if (state.textures.has(key))
    return state.textures.get(key);

  const s = state.manifest.sprites[spriteId];
  const base = ownerId >= 0 ? tintedBase(s.s, ownerId) : neutralBase(s.s);
  const w = Math.abs(s.w), h = Math.abs(s.h);
  const x = s.w < 0 ? s.x + s.w : s.x, y = s.h < 0 ? s.y + s.h : s.y;
  const tex = new PIXI.Texture(base, new PIXI.Rectangle(x, y, w, h));
  tex.orfFlipX = s.w < 0; tex.orfFlipY = s.h < 0;
  tex.orfOx = s.ox; tex.orfOy = s.oy;
  state.textures.set(key, tex);
  return tex;
}

function sequenceOf(typeName, names) {
  const image = state.manifest.images[typeName];
  if (!image) return null;
  for (const n of names)
    if (image[n]) return image[n];
  return null;
}

function spriteIdFor(seq, facing, frame = 0) {
  let idx = 0;
  if (seq.facings > 1 && facing >= 0)
    idx = Math.round(facing * seq.facings / 1024) % seq.facings;
  return seq.ids[(frame % seq.length) * seq.facings + idx];
}

// --- Static decor: trees, initial tiberium ----------------------------------
function drawMapDecor() {
  const ts = state.map.tileSize;
  for (const a of state.map.actors || []) {
    const seq = sequenceOf(a.type, ["idle", "husk"]);
    if (!seq) continue;
    const tex = texture(seq.ids[0], -1);
    if (!tex) continue;
    const sp = new PIXI.Sprite(tex);
    sp.anchor.set(0.5);
    sp.x = (a.x + 0.5) * ts + tex.orfOx;
    sp.y = (a.y + 0.5) * ts + tex.orfOy;
    groundLayer.addChild(sp);
  }

  for (const [u, v, type, index] of state.map.resources || []) {
    // cnc tiberium lives under the "resources" image as sequences ti1..ti12
    // (random visual variant); frame index encodes density.
    const variant = 1 + ((u * 7 + v * 13) % 12);
    const seq = sequenceOf("resources", [`ti${variant}`, "ti1"]);
    if (!seq) continue;
    const frame = Math.min(index, seq.length - 1);
    const tex = texture(seq.ids[frame * seq.facings], -1);
    if (!tex) continue;
    const sp = new PIXI.Sprite(tex);
    sp.anchor.set(0.5);
    sp.x = (u + 0.5) * ts + tex.orfOx;
    sp.y = (v + 0.5) * ts + tex.orfOy;
    groundLayer.addChild(sp);
  }
}

// --- Live feed ---------------------------------------------------------------
function connect() {
  const live = new EventSource("api/live");
  live.onmessage = e => {
    try {
      const d = JSON.parse(e.data);
      if (d.game) {
        const s = d.game.second || 0;
        document.getElementById("clock").textContent =
          `${String(Math.floor(s / 60)).padStart(2, "0")}:${String(Math.floor(s % 60)).padStart(2, "0")}`;
        for (const gp of d.game.players || []) {
          if (gp.colorHex) state.colors[gp.slug] = "#" + gp.colorHex;
          gp.winState === "Lost" ? state.dead.add(gp.slug) : state.dead.delete(gp.slug);
        }
      }
      for (const p of d.players || []) state.names[p.slug] = p.display || p.slug;
      renderLegend();
    } catch (e2) { /* partial frame */ }
  };

  let lastFrameAt = 0;
  const actors = new EventSource("api/actors");
  actors.onmessage = e => {
    try {
      const frame = JSON.parse(e.data);
      const now = performance.now();
      if (lastFrameAt) state.frameInterval = Math.min(500, 0.8 * state.frameInterval + 0.2 * (now - lastFrameAt));
      lastFrameAt = now;
      state.slugOrder = frame.players;
      applyFrame(frame, now);
    } catch (e2) { /* partial frame */ }
  };
}

function renderLegend() {
  document.getElementById("legend").innerHTML = Object.keys(state.names).map(slug =>
    `<span class="${state.dead.has(slug) ? "dead" : ""}"><i style="background:${state.colors[slug] || "#888"}"></i>${state.names[slug]}</span>`).join("");
}

function applyFrame(frame, now) {
  const seen = new Set();
  for (const [id, typeId, ownerId, px, py, pz, facing, turret, hp] of frame.a) {
    seen.add(id);
    const typeName = frame.types[typeId];
    let a = state.actors.get(id);
    if (!a) {
      a = { typeName, ownerId, cur: { x: px, y: py }, from: { x: px, y: py }, to: { x: px, y: py }, t0: now, t1: now, hp, facing, turret };
      const seq = sequenceOf(typeName, ["idle", "stand", "default"]);
      if (!seq) continue; // no drawable sequence (e.g. support actors)
      a.seq = seq;
      a.turretSeq = sequenceOf(typeName, ["turret"]);
      a.sprite = new PIXI.Sprite();
      a.sprite.anchor.set(0.5);
      actorLayer.addChild(a.sprite);
      if (a.turretSeq) {
        a.turretSprite = new PIXI.Sprite();
        a.turretSprite.anchor.set(0.5);
        actorLayer.addChild(a.turretSprite);
      }
      state.actors.set(id, a);
    }

    a.from = { ...a.cur };
    a.to = { x: px, y: py - pz };  // altitude offsets the draw position upward
    a.t0 = now; a.t1 = now + state.frameInterval;
    a.facing = facing; a.turret = turret; a.hp = hp; a.ownerId = ownerId;
  }

  for (const [id, a] of [...state.actors]) {
    if (!seen.has(id)) {
      if (a.sprite) a.sprite.destroy();
      if (a.turretSprite) a.turretSprite.destroy();
      if (a.bar) a.bar.destroy();
      state.actors.delete(id);
    }
  }

  document.getElementById("stats").textContent = `${frame.a.length} actors · ${Math.round(state.frameInterval)}ms/frame`;
}

// --- Render loop: interpolate + retexture ------------------------------------
function tick() {
  const now = performance.now();
  for (const a of state.actors.values()) {
    if (!a.sprite) continue;
    const t = a.t1 > a.t0 ? Math.min(1, (now - a.t0) / (a.t1 - a.t0)) : 1;
    a.cur.x = a.from.x + (a.to.x - a.from.x) * t;
    a.cur.y = a.from.y + (a.to.y - a.from.y) * t;

    setSprite(a.sprite, a.seq, a.facing, a.ownerId);
    a.sprite.x = a.cur.x; a.sprite.y = a.cur.y;
    a.sprite.zIndex = a.cur.y;

    if (a.turretSprite) {
      setSprite(a.turretSprite, a.turretSeq, a.turret >= 0 ? a.turret : a.facing, a.ownerId);
      a.turretSprite.x = a.cur.x; a.turretSprite.y = a.cur.y;
      a.turretSprite.zIndex = a.cur.y + 0.5;
    }

    updateBar(a);
  }
}

function setSprite(sprite, seq, facing, ownerId) {
  const tex = texture(spriteIdFor(seq, facing), ownerId);
  if (!tex) { sprite.visible = false; return; }
  sprite.visible = true;
  if (sprite.texture !== tex) {
    sprite.texture = tex;
    sprite.scale.set(tex.orfFlipX ? -1 : 1, tex.orfFlipY ? -1 : 1);
    sprite.orfOx = tex.orfOx; sprite.orfOy = tex.orfOy;
  }
  sprite.pivot.set(-sprite.orfOx / (tex.orfFlipX ? -1 : 1), -sprite.orfOy / (tex.orfFlipY ? -1 : 1));
}

function updateBar(a) {
  if (a.hp >= 100 || !a.sprite.visible) {
    if (a.bar) a.bar.visible = false;
    return;
  }
  if (!a.bar) { a.bar = new PIXI.Graphics(); fxLayer.addChild(a.bar); }
  const w = Math.max(14, a.sprite.width);
  a.bar.visible = true;
  a.bar.clear();
  a.bar.beginFill(0x000000, 0.7).drawRect(-w / 2, 0, w, 3).endFill();
  a.bar.beginFill(a.hp > 50 ? 0x33cc33 : a.hp > 25 ? 0xddaa22 : 0xcc3333)
    .drawRect(-w / 2 + 0.5, 0.5, (w - 1) * a.hp / 100, 2).endFill();
  a.bar.x = a.cur.x;
  a.bar.y = a.cur.y - a.sprite.height / 2 - 5;
}

boot();
