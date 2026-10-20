import * as THREE from 'three';

/**
 * Inspection modes. Replacement materials are created once per original material and mode, cached, and
 * disposed together with the model. Skinning and morph targets keep working because three.js enables them
 * from the mesh, not from the material.
 */
export class ViewModes {
  constructor() {
    this.mode = 'normal';
    this.originals = new Map(); // mesh -> original material (or array)
    this.cache = new Map(); // original material -> { mode: replacement }
  }

  /** Remember the original materials of a freshly loaded model. */
  track(root) {
    root.traverse((object) => {
      if (object.isMesh || object.isPoints || object.isLine) this.originals.set(object, object.material);
    });
  }

  apply(mode) {
    this.mode = mode;
    for (const [mesh, original] of this.originals) {
      const list = Array.isArray(original) ? original : [original];
      for (const material of list) material.wireframe = mode === 'wireframe' && 'wireframe' in material;
      if (mode === 'normal' || mode === 'wireframe' || !mesh.isMesh) {
        mesh.material = original;
        continue;
      }
      mesh.material = Array.isArray(original) ? original.map((m) => this.replacement(m, mode)) : this.replacement(original, mode);
    }
  }

  replacement(material, mode) {
    let entry = this.cache.get(material);
    if (!entry) {
      entry = {};
      this.cache.set(material, entry);
    }
    if (entry[mode]) return entry[mode];

    const common = { side: material.side, transparent: material.transparent, opacity: material.opacity ?? 1, alphaTest: material.alphaTest ?? 0 };
    let created;
    switch (mode) {
      case 'unlit':
        created = new THREE.MeshBasicMaterial({
          ...common, color: material.color ?? new THREE.Color(1, 1, 1), map: material.map ?? null,
          alphaMap: material.alphaMap ?? null, vertexColors: material.vertexColors ?? false
        });
        break;
      case 'basecolor':
        created = new THREE.MeshBasicMaterial({
          side: material.side, map: material.map ?? null,
          color: material.map ? new THREE.Color(1, 1, 1) : (material.color ?? new THREE.Color(1, 1, 1)),
          alphaTest: material.alphaTest ?? 0
        });
        break;
      case 'normals':
        created = new THREE.MeshNormalMaterial({
          side: material.side, normalMap: material.normalMap ?? null,
          normalScale: material.normalScale ? material.normalScale.clone() : new THREE.Vector2(1, 1)
        });
        break;
      case 'metalrough': {
        // glTF packs roughness in green and metalness in blue; without a texture the factors are shown as a colour.
        const map = material.metalnessMap ?? material.roughnessMap ?? null;
        created = new THREE.MeshBasicMaterial({
          side: material.side, map,
          color: map ? new THREE.Color(1, 1, 1) : new THREE.Color(0, material.roughness ?? 1, material.metalness ?? 0)
        });
        break;
      }
      default:
        created = material;
    }
    entry[mode] = created;
    return created;
  }

  /** Releases the replacement materials (textures belong to the originals and are released with them). */
  dispose() {
    for (const entry of this.cache.values())
      for (const material of Object.values(entry)) material.dispose();
    this.cache.clear();
    this.originals.clear();
  }
}
