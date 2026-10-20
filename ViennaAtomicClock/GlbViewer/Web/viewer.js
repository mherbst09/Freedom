import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';
import { DRACOLoader } from 'three/addons/loaders/DRACOLoader.js';
import { KTX2Loader } from 'three/addons/loaders/KTX2Loader.js';
import { MeshoptDecoder } from 'three/addons/libs/meshopt_decoder.module.js';
import { RoomEnvironment } from 'three/addons/environments/RoomEnvironment.js';
import { validateCommand } from './protocol.js';
import { ViewModes } from './viewmodes.js';

// ───────────────────────── host bridge ─────────────────────────

const host = window.chrome?.webview ?? null;
const post = (message) => { if (host) host.postMessage(message); };

const texts = {
  loading: 'Lade Modell …', parsing: 'Modell wird aufbereitet …', cancelled: 'Laden abgebrochen.',
  missingTexture: 'Textur konnte nicht geladen werden: {0}', badMaterial: 'Material „{0}“ hat einen Fehler.',
  emptyTitle: 'Kein Modell geladen', emptyHint: 'Datei öffnen (Strg+O) oder eine .glb-Datei hierher ziehen', drop: 'Zum Öffnen loslassen',
  of: '{0} von {1}', webgl: 'WebGL ist nicht verfügbar. Bitte Grafiktreiber aktualisieren.'
};
const t = (key, ...args) => (texts[key] ?? key).replace(/\{(\d)\}/g, (_, i) => String(args[Number(i)] ?? ''));

const $ = (id) => document.getElementById(id);
const canvas = $('view');

// ───────────────────────── renderer, scene, camera ─────────────────────────

let renderer;
try {
  renderer = new THREE.WebGLRenderer({ canvas, antialias: true, alpha: true, powerPreference: 'high-performance' });
} catch (error) {
  $('messageText').textContent = t('webgl');
  $('message').hidden = false;
  $('message').classList.add('error');
  post({ type: 'loadFailed', message: t('webgl'), fatal: true });
  throw error;
}
const MAX_PIXEL_RATIO = 1.75;
let pixelRatio = Math.min(window.devicePixelRatio || 1, MAX_PIXEL_RATIO);
renderer.setPixelRatio(pixelRatio);
renderer.outputColorSpace = THREE.SRGBColorSpace;
renderer.toneMapping = THREE.ACESFilmicToneMapping;
renderer.setClearColor(0x000000, 0);
renderer.shadowMap.enabled = true;
renderer.shadowMap.type = THREE.PCFSoftShadowMap;

const scene = new THREE.Scene();
const pmrem = new THREE.PMREMGenerator(renderer);
const environment = pmrem.fromScene(new RoomEnvironment(), 0.04).texture;
pmrem.dispose();
scene.environment = environment;

const camera = new THREE.PerspectiveCamera(45, 1, 0.01, 1000);
camera.position.set(3, 2, 3);

const controls = new OrbitControls(camera, canvas);
controls.enableDamping = true;
controls.dampingFactor = 0.085;
controls.screenSpacePanning = true;
controls.mouseButtons = { LEFT: THREE.MOUSE.ROTATE, MIDDLE: THREE.MOUSE.DOLLY, RIGHT: THREE.MOUSE.PAN };
controls.autoRotateSpeed = 1.6;
controls.addEventListener('change', () => requestRender());

// Studio lighting: key, fill and rim light plus a faint sky/ground fill; scaled to every model.
const lights = new THREE.Group();
const keyLight = new THREE.DirectionalLight(0xffffff, 2.4);
const fillLight = new THREE.DirectionalLight(0xdfe8ff, 0.8);
const rimLight = new THREE.DirectionalLight(0xffffff, 1.3);
const hemiLight = new THREE.HemisphereLight(0xffffff, 0x404048, 0.35);
keyLight.castShadow = true;
keyLight.shadow.mapSize.set(2048, 2048);
keyLight.shadow.bias = -0.0004;
keyLight.shadow.normalBias = 0.02;
lights.add(keyLight, keyLight.target, fillLight, fillLight.target, rimLight, rimLight.target, hemiLight);
scene.add(lights);
const baseIntensity = { key: 2.4, fill: 0.8, rim: 1.3, hemi: 0.35, env: 1 };

const shadowCatcher = new THREE.Mesh(new THREE.PlaneGeometry(1, 1), new THREE.ShadowMaterial({ opacity: 0.28 }));
shadowCatcher.rotation.x = -Math.PI / 2;
shadowCatcher.receiveShadow = true;
shadowCatcher.renderOrder = -1;
scene.add(shadowCatcher);

let grid = null;
let axes = null;
const selectionBox = new THREE.Box3();
const selectionHelper = new THREE.Box3Helper(selectionBox, new THREE.Color(0x4f8bff));
selectionHelper.visible = false;
selectionHelper.material.depthTest = false;
selectionHelper.material.transparent = true;
selectionHelper.material.opacity = 0.75;
selectionHelper.renderOrder = 999;
scene.add(selectionHelper);

// ───────────────────────── loaders (all decoders are local) ─────────────────────────

const draco = new DRACOLoader().setDecoderPath('./lib/jsm/libs/draco/gltf/');
const ktx2 = new KTX2Loader().setTranscoderPath('./lib/jsm/libs/basis/').detectSupport(renderer);

// ───────────────────────── state ─────────────────────────

const state = {
  root: null, gltf: null, mixer: null, actions: [], clipIndex: -1, playing: false, loop: true,
  ids: new Map(), nextId: 1, selected: null, info: null,
  settings: { background: 'Dark', exposure: 1, light: 1, shadows: true, grid: true, axes: false, speed: 1, autoRotate: false },
  initialView: null, radius: 1, center: new THREE.Vector3(), viewMode: 'normal'
};
const viewModes = new ViewModes();
const clock = new THREE.Clock();

// Reused objects: nothing is allocated per frame.
const tmpBox = new THREE.Box3();
const tmpSphere = new THREE.Sphere();
const tmpVector = new THREE.Vector3();
const tween = { active: false, start: 0, duration: 380, fromPos: new THREE.Vector3(), toPos: new THREE.Vector3(), fromTarget: new THREE.Vector3(), toTarget: new THREE.Vector3() };

// ───────────────────────── render loop (only renders when something changed) ─────────────────────────

let needsRender = true;
let frameRequested = false;
let framesInSecond = 0;
let secondStart = performance.now();
let slowFrames = 0;
let fastFrames = 0;
let lastFrame = performance.now();
let lastAnimationReport = 0;

function requestRender() {
  needsRender = true;
  if (!frameRequested) {
    frameRequested = true;
    requestAnimationFrame(frame);
  }
}

function frame(now) {
  frameRequested = false;
  const delta = Math.min(clock.getDelta(), 0.1);
  let continuous = false;

  if (state.mixer && state.playing) {
    state.mixer.update(delta);
    continuous = true;
    if (now - lastAnimationReport > 100) reportAnimation(now);
  }
  if (controls.autoRotate) continuous = true;
  if (tween.active) {
    stepTween(now);
    continuous = true;
  }
  const moved = controls.update(delta);

  if (needsRender || continuous || moved) {
    renderer.render(scene, camera);
    needsRender = false;
    measure(now);
  }
  if (continuous || moved) {
    frameRequested = true;
    requestAnimationFrame(frame);
  } else {
    lastFrame = now;
  }
}

/** FPS report once per second while rendering continuously, and automatic quality steps. */
function measure(now) {
  const frameTime = now - lastFrame;
  lastFrame = now;
  framesInSecond++;
  if (frameTime > 0 && frameTime < 250) {
    if (frameTime > 1000 / 45) { slowFrames++; fastFrames = 0; } else { fastFrames++; slowFrames = Math.max(0, slowFrames - 1); }
  }
  if (slowFrames > 90) adaptQuality(-1);
  else if (fastFrames > 600) adaptQuality(1);
  if (now - secondStart >= 1000) {
    post({ type: 'stats', fps: Math.round(framesInSecond * 1000 / (now - secondStart)), ...memoryStats() });
    framesInSecond = 0;
    secondStart = now;
  }
}

function memoryStats() {
  return {
    geometries: renderer.info.memory.geometries, textures: renderer.info.memory.textures,
    calls: renderer.info.render.calls, triangles: renderer.info.render.triangles, pixelRatio
  };
}

/** Weaker hardware: lower pixel density first, then the shadow resolution (and back up when it is smooth). */
function adaptQuality(direction) {
  slowFrames = 0;
  fastFrames = 0;
  if (direction < 0) {
    if (pixelRatio > 1) pixelRatio = Math.max(1, pixelRatio - 0.25);
    else if (keyLight.shadow.mapSize.x > 512) setShadowSize(keyLight.shadow.mapSize.x / 2);
    else return;
  } else {
    const best = Math.min(window.devicePixelRatio || 1, MAX_PIXEL_RATIO);
    if (keyLight.shadow.mapSize.x < 2048) setShadowSize(keyLight.shadow.mapSize.x * 2);
    else if (pixelRatio < best) pixelRatio = Math.min(best, pixelRatio + 0.25);
    else return;
  }
  renderer.setPixelRatio(pixelRatio);
  resize();
}

function setShadowSize(size) {
  keyLight.shadow.mapSize.set(size, size);
  keyLight.shadow.map?.dispose();
  keyLight.shadow.map = null;
}

function resize() {
  const width = Math.max(1, canvas.clientWidth);
  const height = Math.max(1, canvas.clientHeight);
  renderer.setSize(width, height, false);
  camera.aspect = width / height; // keeps proportions: the model is never stretched
  camera.updateProjectionMatrix();
  requestRender();
}
new ResizeObserver(resize).observe($('stage'));

// ───────────────────────── loading ─────────────────────────

let loadToken = 0;
let loadAbort = null;

async function load({ url, name, size }) {
  cancelLoad(false);
  const token = ++loadToken;
  const abort = new AbortController();
  loadAbort = abort;
  const started = performance.now();
  showLoading(0, size);
  try {
    const response = await fetch(url, { signal: abort.signal, cache: 'no-store' });
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    const total = Number(response.headers.get('Content-Length')) || size || 0;
    const buffer = await readWithProgress(response, total, abort.signal);
    if (token !== loadToken) return;

    $('loadingText').textContent = t('parsing');
    const warnings = [];
    const manager = new THREE.LoadingManager();
    manager.onError = (failed) => warnings.push(t('missingTexture', decodeURIComponent(failed.split('/').pop() || failed)));
    const loader = new GLTFLoader(manager).setDRACOLoader(draco).setKTX2Loader(ktx2).setMeshoptDecoder(MeshoptDecoder);
    const base = url.substring(0, url.lastIndexOf('/') + 1);
    const gltf = await loader.parseAsync(buffer, base);
    if (token !== loadToken || abort.signal.aborted) {
      disposeTree(gltf.scene);
      return;
    }
    install(gltf, name, total, performance.now() - started, warnings);
  } catch (error) {
    if (token !== loadToken) return;
    hideLoading();
    // The scene that was shown before stays untouched.
    post(abort.signal.aborted
      ? { type: 'loadFailed', message: t('cancelled'), cancelled: true }
      : { type: 'loadFailed', message: String(error?.message ?? error) });
  } finally {
    if (loadAbort === abort) loadAbort = null;
  }
}

async function readWithProgress(response, total, signal) {
  if (!response.body) return await response.arrayBuffer();
  const reader = response.body.getReader();
  const chunks = [];
  let loaded = 0;
  let lastReport = 0;
  for (;;) {
    if (signal.aborted) throw new DOMException('aborted', 'AbortError');
    const { done, value } = await reader.read();
    if (done) break;
    chunks.push(value);
    loaded += value.byteLength;
    const now = performance.now();
    if (now - lastReport > 80) {
      lastReport = now;
      showLoading(loaded, total);
      post({ type: 'progress', loaded, total });
    }
  }
  post({ type: 'progress', loaded, total: Math.max(total, loaded) });
  const result = new Uint8Array(loaded);
  let offset = 0;
  for (const chunk of chunks) {
    result.set(chunk, offset);
    offset += chunk.byteLength;
  }
  return result.buffer;
}

function cancelLoad(report = true) {
  if (!loadAbort) return;
  loadAbort.abort();
  loadToken++;
  loadAbort = null;
  hideLoading();
  if (report) post({ type: 'loadFailed', message: t('cancelled'), cancelled: true });
}

function showLoading(loaded, total) {
  $('empty').hidden = true;
  $('loading').hidden = false;
  $('message').hidden = true;
  const share = total > 0 ? Math.min(1, loaded / total) : 0;
  $('loadingBar').style.width = `${(share * 100).toFixed(1)}%`;
  $('loadingDetail').textContent = total > 0 ? t('of', formatBytes(loaded), formatBytes(total)) : formatBytes(loaded);
}

function hideLoading() {
  $('loading').hidden = true;
  $('loadingText').textContent = t('loading');
  $('empty').hidden = !!state.root;
}

function formatBytes(bytes) {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
}

// ───────────────────────── installing and releasing a model ─────────────────────────

function install(gltf, name, size, loadMs, warnings) {
  unloadModel();
  state.gltf = gltf;
  const root = new THREE.Group();
  root.name = '__model__';
  root.add(gltf.scene);
  scene.add(root);
  state.root = root;

  root.traverse((object) => {
    if (object.isMesh) {
      object.castShadow = true;
      object.receiveShadow = true;
      for (const material of materialsOf(object)) checkMaterial(material, warnings);
    }
  });

  // Centre on the ground: x/z in the middle, lowest point on y = 0.
  tmpBox.setFromObject(root, true);
  if (!tmpBox.isEmpty()) {
    const center = tmpBox.getCenter(tmpVector);
    gltf.scene.position.x -= center.x;
    gltf.scene.position.z -= center.z;
    gltf.scene.position.y -= tmpBox.min.y;
    gltf.scene.updateMatrixWorld(true);
  }
  layoutStage();
  viewModes.track(root);
  viewModes.apply(state.viewMode);

  // Animations: one mixer for the model, every clip prepared.
  state.mixer = new THREE.AnimationMixer(gltf.scene);
  state.mixer.addEventListener('finished', () => {
    state.playing = false;
    reportAnimation(performance.now());
  });
  state.actions = gltf.animations.map((clip) => state.mixer.clipAction(clip));
  state.mixer.timeScale = state.settings.speed;
  state.clipIndex = -1;
  state.playing = false;
  if (state.actions.length > 0) playClip(0, true);

  const tree = describe(gltf.scene);
  state.info = collectInfo(gltf, name, size, loadMs);
  hideLoading();
  $('empty').hidden = true;
  applySettings();
  fitView(true);
  post({
    type: 'loaded', info: state.info, tree, warnings,
    animations: gltf.animations.map((clip, index) => ({ index, name: clip.name || `Clip ${index + 1}`, duration: clip.duration })),
    cameras: gltf.cameras.map((c) => ({ id: idOf(c), name: c.name || 'Camera' }))
  });
  requestRender();
  setTimeout(() => post({ type: 'stats', fps: 0, ...memoryStats() }), 50);
}

function materialsOf(mesh) {
  return Array.isArray(mesh.material) ? mesh.material : [mesh.material];
}

function checkMaterial(material, warnings) {
  if (!material) return;
  const maps = ['map', 'normalMap', 'roughnessMap', 'metalnessMap', 'aoMap', 'emissiveMap', 'alphaMap'];
  for (const key of maps) {
    const texture = material[key];
    if (texture && !texture.image && !texture.source?.data && !texture.isCompressedTexture) {
      warnings.push(t('badMaterial', material.name || material.type));
      return;
    }
  }
}

/** Grid, axes, lights and shadow plane follow the model size. */
function layoutStage() {
  tmpBox.setFromObject(state.root, true);
  if (tmpBox.isEmpty()) tmpBox.set(new THREE.Vector3(-1, 0, -1), new THREE.Vector3(1, 1, 1));
  tmpBox.getBoundingSphere(tmpSphere);
  const r = Math.max(tmpSphere.radius, 1e-3);
  state.radius = r;
  state.center.copy(tmpSphere.center);

  const c = tmpSphere.center;
  keyLight.position.set(c.x + r * 2.2, c.y + r * 3.2, c.z + r * 1.6);
  fillLight.position.set(c.x - r * 2.6, c.y + r * 1.2, c.z + r * 1.4);
  rimLight.position.set(c.x - r * 0.6, c.y + r * 2.2, c.z - r * 3);
  for (const light of [keyLight, fillLight, rimLight]) light.target.position.copy(c);
  const shadow = keyLight.shadow.camera;
  shadow.left = -r * 1.6; shadow.right = r * 1.6; shadow.top = r * 1.6; shadow.bottom = -r * 1.6;
  shadow.near = r * 0.1; shadow.far = r * 12;
  shadow.updateProjectionMatrix();

  shadowCatcher.scale.set(r * 8, r * 8, 1);
  shadowCatcher.position.set(c.x, tmpBox.min.y - r * 0.001, c.z);

  if (grid) { scene.remove(grid); grid.geometry.dispose(); grid.material.dispose(); }
  const gridSize = niceSize(r * 4);
  grid = new THREE.GridHelper(gridSize, 20, 0x888888, 0x555555);
  grid.material.transparent = true;
  grid.material.opacity = 0.35;
  grid.material.depthWrite = false;
  grid.position.y = tmpBox.min.y;
  scene.add(grid);
  if (axes) { scene.remove(axes); axes.geometry.dispose(); axes.material.dispose(); }
  axes = new THREE.AxesHelper(r * 1.3);
  axes.material.depthTest = false;
  axes.renderOrder = 998;
  scene.add(axes);
}

function niceSize(value) {
  const power = Math.pow(10, Math.floor(Math.log10(value)));
  return Math.ceil(value / power) * power;
}

function unloadModel() {
  if (!state.root) return;
  if (state.mixer) {
    state.mixer.stopAllAction();
    state.mixer.uncacheRoot(state.gltf.scene);
    for (const clip of state.gltf.animations) state.mixer.uncacheClip(clip);
  }
  viewModes.apply('normal');
  viewModes.dispose();
  scene.remove(state.root);
  disposeTree(state.root);
  state.root = null;
  state.gltf = null;
  state.mixer = null;
  state.actions = [];
  state.clipIndex = -1;
  state.playing = false;
  state.ids.clear();
  state.nextId = 1;
  state.selected = null;
  selectionHelper.visible = false;
  renderer.renderLists.dispose();
}

/** Frees geometries, materials and every texture (including decoded image bitmaps). */
function disposeTree(root) {
  const textures = new Set();
  root.traverse((object) => {
    object.geometry?.dispose?.();
    if (object.material) {
      for (const material of Array.isArray(object.material) ? object.material : [object.material]) {
        for (const value of Object.values(material)) if (value && value.isTexture) textures.add(value);
        material.dispose();
      }
    }
    if (object.skeleton) object.skeleton.dispose();
  });
  for (const texture of textures) {
    const data = texture.source?.data;
    texture.dispose();
    if (data && typeof data.close === 'function') data.close(); // ImageBitmap
  }
}

// ───────────────────────── scene tree, ids and information ─────────────────────────

function idOf(object) {
  if (object.userData.__viewerId) return object.userData.__viewerId;
  const id = state.nextId++;
  object.userData.__viewerId = id;
  state.ids.set(id, object);
  return id;
}

function kindOf(object) {
  if (object.isSkinnedMesh) return 'skinned';
  if (object.isMesh) return 'mesh';
  if (object.isCamera) return 'camera';
  if (object.isLight) return 'light';
  if (object.isBone) return 'bone';
  if (object.isPoints) return 'points';
  if (object.isLine) return 'line';
  return 'node';
}

function describe(object) {
  return {
    id: idOf(object), name: object.name || '', kind: kindOf(object), visible: object.visible,
    children: object.children.filter((child) => !child.isLight || child.parent === object).map(describe)
  };
}

function collectInfo(gltf, name, size, loadMs) {
  const meshes = new Set();
  const materials = new Set();
  const textures = new Set();
  let nodes = 0, vertices = 0, triangles = 0, lightsCount = 0;
  gltf.scene.traverse((object) => {
    if (object === gltf.scene) return;
    nodes++;
    if (object.isLight) lightsCount++;
    if (!object.isMesh) return;
    meshes.add(object);
    const geometry = object.geometry;
    const count = geometry.attributes.position?.count ?? 0;
    const instances = object.isInstancedMesh ? object.count : 1;
    vertices += count * instances;
    triangles += (geometry.index ? geometry.index.count / 3 : count / 3) * instances;
    for (const material of materialsOf(object)) {
      materials.add(material);
      for (const value of Object.values(material)) if (value && value.isTexture) textures.add(value);
    }
  });
  tmpBox.setFromObject(gltf.scene, true);
  const dimensions = tmpBox.isEmpty() ? [0, 0, 0] : tmpBox.getSize(tmpVector).toArray().map((v) => round(v));
  const json = gltf.parser?.json ?? {};
  return {
    fileName: name, fileSize: size, scenes: (json.scenes ?? [gltf.scene]).length, nodes, meshes: meshes.size,
    materials: materials.size, textures: textures.size, animations: gltf.animations.length, cameras: gltf.cameras.length,
    lights: lightsCount, vertices, triangles: Math.round(triangles), dimensions, loadMs: Math.round(loadMs),
    extensions: json.extensionsUsed ?? [], generator: json.asset?.generator ?? ''
  };
}

const round = (v) => Math.round(v * 10000) / 10000;

function properties(object) {
  let vertices = 0, triangles = 0;
  const materialNames = [];
  object.traverse((child) => {
    if (!child.isMesh) return;
    const count = child.geometry.attributes.position?.count ?? 0;
    vertices += count;
    triangles += child.geometry.index ? child.geometry.index.count / 3 : count / 3;
  });
  if (object.isMesh) {
    const original = viewModes.originals.get(object) ?? object.material;
    for (const material of Array.isArray(original) ? original : [original])
      materialNames.push(`${material.name || '(ohne Name)'} · ${material.type}${material.transparent ? ' · transparent' : ''}${material.alphaTest > 0 ? ' · alpha mask' : ''}${material.side === THREE.DoubleSide ? ' · doppelseitig' : ''}`);
  }
  tmpBox.setFromObject(object, true);
  const size = tmpBox.isEmpty() ? [0, 0, 0] : tmpBox.getSize(tmpVector).toArray().map(round);
  return {
    id: idOf(object), name: object.name || '', kind: kindOf(object), type: object.type,
    position: object.position.toArray().map(round),
    rotation: [object.rotation.x, object.rotation.y, object.rotation.z].map((r) => round(THREE.MathUtils.radToDeg(r))),
    scale: object.scale.toArray().map(round),
    materials: materialNames, vertices, triangles: Math.round(triangles), size, visible: object.visible
  };
}

// ───────────────────────── selection, focus, camera ─────────────────────────

function select(id, report = true) {
  const object = id === null ? null : state.ids.get(id) ?? null;
  state.selected = object;
  updateSelectionHelper();
  if (report) post({ type: 'selection', id: object ? id : null, props: object ? properties(object) : null });
  requestRender();
}

function updateSelectionHelper() {
  if (!state.selected) {
    selectionHelper.visible = false;
    return;
  }
  selectionBox.setFromObject(state.selected, true);
  selectionHelper.visible = !selectionBox.isEmpty();
}

function boundsOf(object) {
  tmpBox.setFromObject(object ?? state.root ?? scene, true);
  if (tmpBox.isEmpty()) tmpBox.set(new THREE.Vector3(-1, -1, -1), new THREE.Vector3(1, 1, 1));
  tmpBox.getBoundingSphere(tmpSphere);
  return tmpSphere;
}

/** Smoothly frames an object (or the whole model) from the current viewing direction. */
function focus(object, animate = true) {
  const sphere = boundsOf(object);
  const radius = Math.max(sphere.radius, 1e-4);
  const distance = radius / Math.sin(THREE.MathUtils.degToRad(camera.fov) / 2) * 1.15;
  const direction = tmpVector.copy(camera.position).sub(controls.target);
  if (direction.lengthSq() < 1e-12) direction.set(1, 0.6, 1);
  direction.normalize();
  tween.toTarget.copy(sphere.center);
  tween.toPos.copy(sphere.center).addScaledVector(direction, distance);
  // The camera may not come closer than the object's own size: it never flies into the model.
  controls.minDistance = radius * 0.6;
  controls.maxDistance = Math.max(distance * 20, state.radius * 30);
  camera.near = Math.max(distance / 1000, radius / 200);
  camera.far = Math.max(distance * 100, state.radius * 100);
  camera.updateProjectionMatrix();
  if (!animate || window.matchMedia('(prefers-reduced-motion: reduce)').matches) {
    camera.position.copy(tween.toPos);
    controls.target.copy(tween.toTarget);
    tween.active = false;
    controls.update();
    requestRender();
    return;
  }
  tween.fromPos.copy(camera.position);
  tween.fromTarget.copy(controls.target);
  tween.start = performance.now();
  tween.active = true;
  requestRender();
}

function stepTween(now) {
  const p = Math.min(1, (now - tween.start) / tween.duration);
  const e = 1 - Math.pow(1 - p, 3);
  camera.position.lerpVectors(tween.fromPos, tween.toPos, e);
  controls.target.lerpVectors(tween.fromTarget, tween.toTarget, e);
  if (p >= 1) tween.active = false;
}

/** First view after loading: whole model, slightly from above and the side. */
function fitView(store) {
  const sphere = boundsOf(null);
  camera.fov = 45;
  const distance = sphere.radius / Math.sin(THREE.MathUtils.degToRad(camera.fov) / 2) * 1.15;
  controls.target.copy(sphere.center);
  camera.position.copy(sphere.center).add(tmpVector.set(1, 0.55, 1.15).normalize().multiplyScalar(distance));
  focus(null, false);
  if (store) state.initialView = { position: camera.position.clone(), target: controls.target.clone(), fov: camera.fov };
}

function resetView() {
  if (!state.initialView) return fitView(true);
  tween.fromPos.copy(camera.position);
  tween.fromTarget.copy(controls.target);
  tween.toPos.copy(state.initialView.position);
  tween.toTarget.copy(state.initialView.target);
  camera.fov = state.initialView.fov;
  controls.minDistance = state.radius * 0.6;
  camera.updateProjectionMatrix();
  tween.start = performance.now();
  tween.active = true;
  requestRender();
}

function centerModel() {
  if (!state.root) return;
  const model = state.gltf.scene;
  tmpBox.setFromObject(state.root, true);
  if (!tmpBox.isEmpty()) {
    const center = tmpBox.getCenter(new THREE.Vector3());
    model.position.x -= center.x;
    model.position.z -= center.z;
    model.position.y -= tmpBox.min.y;
    model.updateMatrixWorld(true);
  }
  layoutStage();
  applySettings();
  updateSelectionHelper();
  focus(null);
}

/** Looks through a camera stored in the GLB (fov and world transform). */
function useCamera(id) {
  const source = state.ids.get(id);
  if (!source || !source.isCamera) return;
  source.updateMatrixWorld(true);
  source.getWorldPosition(tween.toPos);
  const forward = new THREE.Vector3(0, 0, -1).applyQuaternion(source.getWorldQuaternion(new THREE.Quaternion()));
  const sphere = boundsOf(null);
  const distance = Math.max(sphere.center.distanceTo(tween.toPos), state.radius * 0.5);
  tween.toTarget.copy(tween.toPos).addScaledVector(forward, distance);
  if (source.isPerspectiveCamera) camera.fov = source.fov;
  camera.updateProjectionMatrix();
  controls.minDistance = 0;
  tween.fromPos.copy(camera.position);
  tween.fromTarget.copy(controls.target);
  tween.start = performance.now();
  tween.active = true;
  requestRender();
}

// ───────────────────────── settings ─────────────────────────

function applySettings() {
  const s = state.settings;
  document.body.className = `bg-${s.background.toLowerCase()}`;
  renderer.toneMappingExposure = s.exposure;
  keyLight.intensity = baseIntensity.key * s.light;
  fillLight.intensity = baseIntensity.fill * s.light;
  rimLight.intensity = baseIntensity.rim * s.light;
  hemiLight.intensity = baseIntensity.hemi * s.light;
  scene.environmentIntensity = baseIntensity.env * Math.max(0.15, s.light);
  keyLight.castShadow = s.shadows;
  shadowCatcher.visible = s.shadows && !!state.root;
  shadowCatcher.material.opacity = s.background === 'Light' ? 0.18 : 0.3;
  if (grid) {
    grid.visible = s.grid && !!state.root;
    grid.material.color.set(s.background === 'Light' ? 0x9aa0aa : 0x6b707a);
  }
  if (axes) axes.visible = s.axes && !!state.root;
  controls.autoRotate = s.autoRotate;
  if (state.mixer) state.mixer.timeScale = s.speed;
  requestRender();
}

function reportState() {
  const s = state.settings;
  post({ type: 'state', grid: s.grid, axes: s.axes, autoRotate: s.autoRotate, viewMode: state.viewMode });
}

function setViewMode(mode) {
  state.viewMode = mode;
  viewModes.apply(mode);
  requestRender();
}

// ───────────────────────── animation ─────────────────────────

function playClip(index, immediate = false) {
  const next = state.actions[index];
  if (!next) return;
  const previous = state.actions[state.clipIndex];
  next.reset();
  next.setLoop(state.loop ? THREE.LoopRepeat : THREE.LoopOnce, Infinity);
  next.clampWhenFinished = true;
  next.enabled = true;
  next.paused = false;
  next.play();
  if (previous && previous !== next && !immediate) previous.crossFadeTo(next, 0.35, false); // soft blend between clips
  else if (previous && previous !== next) previous.stop();
  state.clipIndex = index;
  state.playing = true;
  requestRender();
  reportAnimation(performance.now());
}

function animationCommand(action, value) {
  const current = state.actions[state.clipIndex];
  switch (action) {
    case 'clip':
      if (value < state.actions.length) playClip(value);
      return;
    case 'speed':
      state.settings.speed = value;
      if (state.mixer) state.mixer.timeScale = value;
      break;
    case 'loop':
      state.loop = value;
      for (const a of state.actions) a.setLoop(value ? THREE.LoopRepeat : THREE.LoopOnce, Infinity);
      break;
  }
  if (!current) return reportAnimation(performance.now());
  switch (action) {
    case 'play':
      if (!current.isRunning() && current.time >= current.getClip().duration - 1e-4) current.reset();
      current.paused = false;
      current.play();
      state.playing = true;
      clock.getDelta();
      break;
    case 'pause':
      current.paused = true;
      state.playing = false;
      break;
    case 'toggle':
      return animationCommand(state.playing ? 'pause' : 'play');
    case 'stop':
      current.reset();
      current.play();
      current.paused = true;
      state.playing = false;
      state.mixer.update(0);
      break;
    case 'seek':
      current.time = Math.min(value, current.getClip().duration);
      current.enabled = true;
      current.play();
      if (!state.playing) current.paused = true;
      state.mixer.update(0);
      break;
  }
  updateSelectionHelper();
  requestRender();
  reportAnimation(performance.now());
}

function reportAnimation(now) {
  lastAnimationReport = now;
  const current = state.actions[state.clipIndex];
  post({
    type: 'animation', clip: state.clipIndex, time: current ? current.time : 0, duration: current ? current.getClip().duration : 0,
    playing: state.playing, loop: state.loop, speed: state.settings.speed
  });
}

// ───────────────────────── screenshots ─────────────────────────

function screenshot(target, transparent) {
  const helperVisible = selectionHelper.visible;
  selectionHelper.visible = false;
  renderer.render(scene, camera);
  const width = canvas.width;
  const height = canvas.height;
  let output = canvas;
  if (!transparent && state.settings.background !== 'Transparent') {
    // The CSS background is not part of the WebGL image: paint the same gradient below it.
    output = document.createElement('canvas');
    output.width = width;
    output.height = height;
    const ctx = output.getContext('2d');
    const gradient = ctx.createRadialGradient(width / 2, height * 0.35, 0, width / 2, height * 0.35, Math.max(width, height) * 0.75);
    const stops = {
      Dark: ['#2c313b', '#1a1d24', '#111318'], Light: ['#ffffff', '#eef0f4', '#d7dbe2'], Studio: ['#8a9099', '#5a5f68', '#2e3138']
    }[state.settings.background];
    gradient.addColorStop(0, stops[0]);
    gradient.addColorStop(0.55, stops[1]);
    gradient.addColorStop(1, stops[2]);
    ctx.fillStyle = gradient;
    ctx.fillRect(0, 0, width, height);
    ctx.drawImage(canvas, 0, 0);
  }
  const data = output.toDataURL('image/png').split(',')[1];
  selectionHelper.visible = helperVisible;
  requestRender();
  post({ type: 'screenshot', target, data, width, height });
}

// ───────────────────────── commands from Freedom ─────────────────────────

const commands = {
  load: (m) => load(m),
  cancelLoad: () => cancelLoad(true),
  unload: () => { cancelLoad(false); unloadModel(); hideLoading(); $('empty').hidden = false; requestRender(); },
  resetView,
  centerModel,
  showAll: () => {
    state.root?.traverse((o) => { o.visible = true; });
    updateSelectionHelper();
    requestRender();
  },
  requestStats: () => post({ type: 'stats', fps: 0, ...memoryStats() }),
  settings: (m) => {
    Object.assign(state.settings, {
      background: m.background, exposure: m.exposure, light: m.light, shadows: m.shadows,
      grid: m.grid, axes: m.axes, speed: m.speed, autoRotate: m.autoRotate
    });
    applySettings();
  },
  focus: (m) => focus(m.id === null ? state.selected : state.ids.get(m.id) ?? null),
  select: (m) => select(m.id),
  setVisible: (m) => {
    const object = state.ids.get(m.id);
    if (!object) return;
    object.visible = m.visible;
    updateSelectionHelper();
    requestRender();
    if (state.selected === object) post({ type: 'selection', id: m.id, props: properties(object) });
  },
  setTransform: (m) => {
    const object = state.ids.get(m.id);
    if (!object) return;
    object.position.fromArray(m.position);
    object.rotation.set(...m.rotation.map((d) => THREE.MathUtils.degToRad(d)));
    object.scale.fromArray(m.scale);
    object.updateMatrixWorld(true);
    updateSelectionHelper();
    requestRender();
    post({ type: 'selection', id: m.id, props: properties(object) });
  },
  viewMode: (m) => { setViewMode(m.mode); reportState(); },
  animation: (m) => animationCommand(m.action, m.value),
  screenshot: (m) => screenshot(m.target, m.transparent),
  useCamera: (m) => useCamera(m.id),
  language: (m) => {
    Object.assign(texts, m.texts);
    $('emptyTitle').textContent = t('emptyTitle');
    $('emptyHint').textContent = t('emptyHint');
    $('dropText').textContent = t('drop');
    $('loadingText').textContent = t('loading');
  }
};

host?.addEventListener('message', (event) => {
  const command = validateCommand(event.data);
  if (!command) {
    post({ type: 'warning', message: 'Ignored invalid command' });
    return;
  }
  try {
    commands[command.type](command);
  } catch (error) {
    post({ type: 'warning', message: String(error?.message ?? error) });
  }
});

// ───────────────────────── mouse and keyboard ─────────────────────────

const raycaster = new THREE.Raycaster();
const pointer = new THREE.Vector2();

canvas.addEventListener('dblclick', (event) => {
  if (!state.root) return;
  const rect = canvas.getBoundingClientRect();
  pointer.set(((event.clientX - rect.left) / rect.width) * 2 - 1, -((event.clientY - rect.top) / rect.height) * 2 + 1);
  raycaster.setFromCamera(pointer, camera);
  const hit = raycaster.intersectObject(state.root, true).find((h) => isShown(h.object));
  if (!hit) return;
  select(idOf(hit.object));
  focus(hit.object);
});

function isShown(object) {
  for (let o = object; o; o = o.parent) if (!o.visible) return false;
  return true;
}

canvas.addEventListener('contextmenu', (event) => event.preventDefault());
canvas.addEventListener('pointerdown', () => canvas.focus());

window.addEventListener('keydown', (event) => {
  const key = event.key.toLowerCase();
  if (event.ctrlKey && key === 'o') { post({ type: 'key', key: 'ctrl+o' }); event.preventDefault(); return; }
  if (event.key === 'F11') { post({ type: 'key', key: 'f11' }); event.preventDefault(); return; }
  if (event.key === 'Escape') { post({ type: 'key', key: 'escape' }); event.preventDefault(); return; }
  if (event.ctrlKey || event.altKey || event.metaKey) return;
  switch (key) {
    case ' ': animationCommand('toggle'); break;
    case 'r': resetView(); break;
    case 'f': focus(state.selected); break;
    case 'g': state.settings.grid = !state.settings.grid; applySettings(); reportState(); break;
    case 'a': state.settings.autoRotate = !state.settings.autoRotate; applySettings(); reportState(); break;
    case 'w': setViewMode(state.viewMode === 'wireframe' ? 'normal' : 'wireframe'); reportState(); break;
    default: return;
  }
  event.preventDefault();
});

// Files dropped on the viewport are handed to Freedom (with their path) – the page never reads them itself.
let dragDepth = 0;
document.addEventListener('dragenter', (event) => { event.preventDefault(); dragDepth++; $('dropHint').hidden = false; });
document.addEventListener('dragleave', () => { dragDepth = Math.max(0, dragDepth - 1); if (dragDepth === 0) $('dropHint').hidden = true; });
document.addEventListener('dragover', (event) => { event.preventDefault(); if (event.dataTransfer) event.dataTransfer.dropEffect = 'copy'; });
document.addEventListener('drop', (event) => {
  event.preventDefault();
  dragDepth = 0;
  $('dropHint').hidden = true;
  const files = [...(event.dataTransfer?.files ?? [])];
  if (files.length > 0 && host?.postMessageWithAdditionalObjects) host.postMessageWithAdditionalObjects({ type: 'drop' }, files);
});

document.addEventListener('visibilitychange', () => { if (!document.hidden) requestRender(); });

resize();
post({ type: 'ready' });
