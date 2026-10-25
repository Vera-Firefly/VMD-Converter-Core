// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 MMD Play Mode Preview contributors

import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const FLAGS = {
  tailIndex: 0x0001,
  rotatable: 0x0002,
  movable: 0x0004,
  visible: 0x0008,
  enabled: 0x0010,
  ik: 0x0020,
  inheritRotation: 0x0100,
  fixedAxis: 0x0400,
};

const ROTATE = FLAGS.rotatable | FLAGS.visible | FLAGS.enabled;
const MOVE_ROTATE = ROTATE | FLAGS.movable;

const bones = [];
function addBone(name, english, position, parent, options = {}) {
  bones.push({
    name,
    english,
    position,
    parent,
    layer: options.layer ?? 0,
    flags: options.flags ?? ROTATE,
    tail: options.tail ?? [0, 1, 0],
    inherit: options.inherit,
    fixedAxis: options.fixedAxis,
    ik: options.ik,
  });
}

// Standard-bone positions and IK geometry are calibrated from the local Miku
// compatibility model. Character mesh, materials, textures, physics, and
// character-specific bones are intentionally excluded. A tiny transparent
// triangle remains because the legacy PMX2FBX backend rejects zero vertices.
addBone("全ての親", "motherbone", [0.000006910376, 0.0249763168, 0], null, { flags: MOVE_ROTATE, tail: "センター" });
addBone("センター", "center", [0.000006910376, 0, 0], "全ての親", { flags: MOVE_ROTATE, tail: "グルーブ" });
addBone("グルーブ", "groove", [0, 8.61582, 0], "センター", { flags: MOVE_ROTATE, tail: "腰" });
addBone("腰", "waist", [0, 12.669425, 0.06453264], "グルーブ", { tail: "上半身" });

function addLegIk(side, positions) {
  const englishSide = side === "左" ? "left" : "right";
  addBone(`${side}足IK親`, `${englishSide} leg IK parent`, positions.ikParent, "全ての親", {
    flags: MOVE_ROTATE, tail: `${side}足ＩＫ`,
  });
  addBone(`${side}足ＩＫ`, `${side}足IK`, positions.legIk, `${side}足IK親`, {
    flags: MOVE_ROTATE | FLAGS.ik, layer: 1,
    tail: [0, 0, -1],
    ik: {
      target: `${side}足首`, iterations: 40, angle: 2.00000072,
      links: [
        { bone: `${side}ひざ`, min: [-Math.PI, 0, 0], max: [-Math.PI / 360, 0, 0] },
        { bone: `${side}足` },
      ],
    },
  });
  addBone(`${side}つま先ＩＫ`, `${side}つま先IK`, positions.toeIk, `${side}足ＩＫ`, {
    flags: MOVE_ROTATE | FLAGS.ik, layer: 1,
    tail: [0, 0, -0.8],
    ik: {
      target: `${side}つま先`, iterations: 3, angle: 4.0,
      links: [{ bone: `${side}足首` }],
    },
  });
}

const leftLeg = {
  cancel: [0.8382071, 11.4989, -0.4166958],
  hip: [0.8382071, 11.4988966, -0.4166958],
  knee: [0.74665457, 7.07590961, -0.351474464],
  ankle: [0.760116339, 1.86846352, 0.438994],
  toe: [0.6991606, 0.266519338, -1.2956115],
  ikParent: [0.6497153, 0.0739434958, 0.445135117],
  legIk: [0.7597841, 1.82808542, 0.445135117],
  toeIk: [0.6943183, 0.139293075, -1.43340921],
};
const rightLeg = {
  cancel: [-0.9148456, 11.4535637, -0.541083932],
  hip: [-0.9148456, 11.45356, -0.541083932],
  knee: [-0.746615469, 7.073816, -0.363333],
  ankle: [-0.7604327, 1.8681668, 0.438883215],
  toe: [-0.6990167, 0.262155265, -1.30035543],
  ikParent: [-0.6497153, 0.0739434958, 0.445135117],
  legIk: [-0.7597841, 1.82808542, 0.445135117],
  toeIk: [-0.6943183, 0.139293075, -1.43340921],
};
addLegIk("左", leftLeg);
addLegIk("右", rightLeg);

addBone("上半身", "upper body", [0, 13.5874844, -0.8964927], "腰", { tail: "上半身2" });
addBone("上半身2", "upper body2", [0, 14.7240982, -0.8886812], "上半身", { tail: "首" });
addBone("下半身", "lower body", [0, 13.5269852, -0.8964826], "腰", { tail: [0, -2, 0] });
addBone("首", "neck", [0, 16.9, 0], "上半身2", { tail: "頭" });
addBone("頭", "head", [0, 17.7, -0.15], "首", { tail: [0, 2, 0] });
addBone("両目", "eyes", [0, 18.5, -0.49], "頭", { tail: [0, 0, -1] });
addBone("左目", "left eye", [0.3, 18.5, -0.49], "頭", {
  tail: [0, 0, -0.5], flags: ROTATE | FLAGS.inheritRotation,
  inherit: { parent: "両目", weight: 1 },
});
addBone("右目", "right eye", [-0.3, 18.5, -0.49], "頭", {
  tail: [0, 0, -0.5], flags: ROTATE | FLAGS.inheritRotation,
  inherit: { parent: "両目", weight: 1 },
});

function addArm(side, sign) {
  const shoulder = `${side}肩`;
  const arm = `${side}腕`;
  const elbow = `${side}ひじ`;
  const wrist = `${side}手首`;
  const englishSide = side === "左" ? "left" : "right";
  const armTwist = `${side}腕捩`;
  const armTwist25 = `${side}腕捩+`;
  const armTwist50 = `${side}腕捩++`;
  const armTwist75 = `${side}腕捩+++`;
  const handTwist = `${side}手捩`;
  addBone(shoulder, `${englishSide} shoulder`, [0.6 * sign, 16.6, -0.06], "上半身2", { tail: arm });
  addBone(arm, `${englishSide} arm`, [1.2 * sign, 16.5, -0.04], shoulder, { tail: elbow });
  addBone(armTwist, `${englishSide} arm twist`, [2.3 * sign, 15.6, -0.02], arm, {
    tail: elbow, fixedAxis: [0.8 * sign, -0.6, 0],
  });
  addBone(armTwist25, `${englishSide} arm twist 25`, [1.8 * sign, 16.0, -0.03], arm, {
    tail: elbow, flags: ROTATE | FLAGS.inheritRotation,
    inherit: { parent: armTwist, weight: 0.25 },
  });
  addBone(armTwist50, `${englishSide} arm twist 50`, [2.3 * sign, 15.6, -0.02], arm, {
    tail: elbow, flags: ROTATE | FLAGS.inheritRotation,
    inherit: { parent: armTwist, weight: 0.5 },
  });
  addBone(armTwist75, `${englishSide} arm twist 75`, [2.8 * sign, 15.2, -0.01], arm, {
    tail: elbow, flags: ROTATE | FLAGS.inheritRotation,
    inherit: { parent: armTwist, weight: 0.75 },
  });
  // Preserve the legacy reference rig's asymmetric deformation chain. Some
  // widely distributed VMDs were authored against this behavior: the left
  // elbow receives the 75% twist grant while the right receives the full twist.
  const elbowParent = side === "左" ? armTwist75 : armTwist;
  addBone(elbow, `${englishSide} elbow`, [3.2 * sign, 14.9, 0], elbowParent, { tail: wrist });
  addBone(handTwist, `${englishSide} hand twist`, [4.2 * sign, 14.1, -0.15], elbow, {
    tail: wrist, fixedAxis: [0.8 * sign, -0.6, 0],
  });
  addBone(wrist, `${englishSide} wrist`, [5.1 * sign, 13.4, -0.25], handTwist, { tail: [0.7 * sign, 0, 0] });

  const fingerRows = [
    ["親指０", "thumb0", [5.3, 13.15, -0.40]],
    ["親指１", "thumb1", [5.55, 12.95, -0.55]],
    ["親指２", "thumb2", [5.85, 12.75, -0.65]],
    ["人指１", "index1", [5.55, 13.15, -0.45]],
    ["人指２", "index2", [6.0, 13.0, -0.50]],
    ["人指３", "index3", [6.35, 12.85, -0.52]],
    ["中指１", "middle1", [5.55, 13.2, -0.25]],
    ["中指２", "middle2", [6.05, 13.05, -0.27]],
    ["中指３", "middle3", [6.45, 12.9, -0.28]],
    ["薬指１", "ring1", [5.5, 13.2, -0.05]],
    ["薬指２", "ring2", [5.95, 13.05, -0.04]],
    ["薬指３", "ring3", [6.3, 12.9, -0.03]],
    ["小指１", "little1", [5.4, 13.15, 0.12]],
    ["小指２", "little2", [5.8, 13.0, 0.15]],
    ["小指３", "little3", [6.1, 12.85, 0.17]],
  ];
  for (let row = 0; row < fingerRows.length; row += 3) {
    for (let segment = 0; segment < 3; segment++) {
      const [suffix, englishSuffix, sourcePosition] = fingerRows[row + segment];
      const name = `${side}${suffix}`;
      const parent = segment === 0 ? wrist : `${side}${fingerRows[row + segment - 1][0]}`;
      const next = segment < 2 ? `${side}${fingerRows[row + segment + 1][0]}` : null;
      const position = [sourcePosition[0] * sign, sourcePosition[1], sourcePosition[2]];
      addBone(name, `${englishSide} ${englishSuffix}`, position, parent, {
        tail: next ?? [0.35 * sign, 0, 0],
      });
    }
  }
}

addArm("左", 1);
addArm("右", -1);

function addLeg(side, positions) {
  const englishSide = side === "左" ? "left" : "right";
  const cancelName = `腰キャンセル${side}`;
  addBone(cancelName, `${englishSide} waist cancel`, positions.cancel, "下半身", {
    flags: FLAGS.rotatable | FLAGS.enabled | FLAGS.inheritRotation,
    tail: [0, -1, 0],
    inherit: { parent: "腰", weight: -1 },
  });
  addBone(`${side}足`, `${englishSide} leg`, positions.hip, cancelName, { tail: `${side}ひざ` });
  addBone(`${side}ひざ`, `${englishSide} knee`, positions.knee, `${side}足`, { tail: `${side}足首` });
  addBone(`${side}足首`, `${englishSide} ankle`, positions.ankle, `${side}ひざ`, { tail: `${side}つま先` });
  addBone(`${side}つま先`, `${englishSide} toe`, positions.toe, `${side}足首`, { tail: [0, 0, -0.8] });
}

addLeg("左", leftLeg);
addLeg("右", rightLeg);

const morphs = [
  ["真面目", "serious", 1], ["困る", "troubled", 1], ["にこり", "brow smile", 1],
  ["怒り", "angry", 1], ["上", "brow up", 1], ["下", "brow down", 1],
  ["まばたき", "blink", 2], ["笑い", "eye smile", 2], ["ウィンク", "wink", 2],
  ["ウィンク右", "wink right", 2], ["びっくり", "surprised", 2], ["じと目", "doubt", 2],
  ["あ", "a", 3], ["い", "i", 3], ["う", "u", 3], ["え", "e", 3], ["お", "o", 3],
  ["にやり", "grin", 3], ["口角上げ", "mouth corners up", 3], ["口角下げ", "mouth corners down", 3],
];

const boneIndex = new Map(bones.map((bone, index) => [bone.name, index]));
for (const bone of bones) {
  if (bone.parent !== null && !boneIndex.has(bone.parent)) throw new Error(`Unknown parent: ${bone.parent}`);
  if (typeof bone.tail === "string" && !boneIndex.has(bone.tail)) throw new Error(`Unknown tail: ${bone.tail}`);
  if (bone.inherit && !boneIndex.has(bone.inherit.parent)) throw new Error(`Unknown inherit parent: ${bone.inherit.parent}`);
  if (bone.ik) {
    if (!boneIndex.has(bone.ik.target)) throw new Error(`Unknown IK target: ${bone.ik.target}`);
    for (const link of bone.ik.links) if (!boneIndex.has(link.bone)) throw new Error(`Unknown IK link: ${link.bone}`);
  }
}

class Writer {
  constructor() { this.parts = []; }
  bytes(value) { this.parts.push(Buffer.from(value)); }
  u8(value) { const b = Buffer.allocUnsafe(1); b.writeUInt8(value); this.parts.push(b); }
  i8(value) { const b = Buffer.allocUnsafe(1); b.writeInt8(value); this.parts.push(b); }
  u16(value) { const b = Buffer.allocUnsafe(2); b.writeUInt16LE(value); this.parts.push(b); }
  i16(value) { const b = Buffer.allocUnsafe(2); b.writeInt16LE(value); this.parts.push(b); }
  i32(value) { const b = Buffer.allocUnsafe(4); b.writeInt32LE(value); this.parts.push(b); }
  f32(value) { const b = Buffer.allocUnsafe(4); b.writeFloatLE(value); this.parts.push(b); }
  string(value) { const b = Buffer.from(value, "utf8"); this.i32(b.length); this.parts.push(b); }
  vec3(value) { this.f32(value[0]); this.f32(value[1]); this.f32(value[2]); }
  result() { return Buffer.concat(this.parts); }
}

function indexOf(name) { return name === null ? -1 : boneIndex.get(name); }

function generatePmx() {
  const w = new Writer();
  w.bytes(Buffer.from("PMX ", "ascii"));
  w.f32(2.0);
  w.u8(8);
  w.bytes([1, 0, 1, 1, 1, 2, 1, 1]); // UTF-8, no extra UVs, index sizes.
  w.string("MMD標準ヒューマノイド参照リグ");
  w.string("MMD Standard Humanoid Reference Rig");
  w.string("Apache-2.0のVMD変換用参照リグです。標準ボーン位置とIK配置はローカル互換モデルに合わせています。キャラクターメッシュ、テクスチャ、物理データは含みません。");
  w.string("Apache-2.0 VMD conversion reference rig. Standard-bone and IK positions are calibrated to a local compatibility model; no character mesh, textures, or physics data is included.");
  const compatibilityVertices = [[0, 0, 0], [0.001, 0, 0], [0, 0.001, 0]];
  w.i32(compatibilityVertices.length);
  for (const position of compatibilityVertices) {
    w.vec3(position);
    w.vec3([0, 0, -1]);
    w.f32(0); w.f32(0); // UV
    w.u8(0); // BDEF1
    w.i16(indexOf("全ての親"));
    w.f32(0); // edge scale
  }
  w.i32(3); // one triangle
  w.u8(0); w.u8(1); w.u8(2);
  w.i32(0); // textures
  w.i32(1); // one transparent compatibility material
  w.string("互換用透明材質"); w.string("Transparent compatibility material");
  w.f32(0); w.f32(0); w.f32(0); w.f32(0); // diffuse RGBA
  w.vec3([0, 0, 0]); w.f32(0); // specular and power
  w.vec3([0, 0, 0]);
  w.u8(0);
  w.f32(0); w.f32(0); w.f32(0); w.f32(0); // edge color
  w.f32(0);
  w.i8(-1); w.i8(-1); // texture and sphere texture
  w.u8(0); // sphere mode
  w.u8(1); w.u8(0); // shared toon 01
  w.string("Required only because the legacy PMX2FBX backend rejects zero-vertex PMX files.");
  w.i32(3);

  w.i32(bones.length);
  for (const bone of bones) {
    const flags = bone.flags |
      (typeof bone.tail === "string" ? FLAGS.tailIndex : 0) |
      (bone.fixedAxis ? FLAGS.fixedAxis : 0);
    w.string(bone.name);
    w.string(bone.english);
    w.vec3(bone.position);
    w.i16(indexOf(bone.parent));
    w.i32(bone.layer);
    w.u16(flags);
    if (flags & FLAGS.tailIndex) w.i16(indexOf(bone.tail)); else w.vec3(bone.tail);
    if (flags & FLAGS.inheritRotation) {
      w.i16(indexOf(bone.inherit.parent));
      w.f32(bone.inherit.weight);
    }
    if (bone.fixedAxis) {
      w.vec3(bone.fixedAxis);
    }
    if (flags & FLAGS.ik) {
      w.i16(indexOf(bone.ik.target));
      w.i32(bone.ik.iterations);
      w.f32(bone.ik.angle);
      w.i32(bone.ik.links.length);
      for (const link of bone.ik.links) {
        w.i16(indexOf(link.bone));
        w.u8(link.min ? 1 : 0);
        if (link.min) { w.vec3(link.min); w.vec3(link.max); }
      }
    }
  }

  w.i32(morphs.length);
  for (const [name, english, panel] of morphs) {
    w.string(name);
    w.string(english);
    w.u8(panel);
    w.u8(1); // Vertex morph; zero offsets intentionally provide only the interoperable name.
    w.i32(0);
  }

  w.i32(2); // display frames
  w.string("Root"); w.string("Root"); w.u8(1); w.i32(1); w.u8(0); w.i16(indexOf("全ての親"));
  w.string("表情"); w.string("Facial"); w.u8(1); w.i32(morphs.length);
  for (let index = 0; index < morphs.length; index++) { w.u8(1); w.i8(index); }
  w.i32(0); // rigid bodies
  w.i32(0); // joints
  return w.result();
}

class Reader {
  constructor(buffer) { this.buffer = buffer; this.offset = 0; }
  take(size) { const b = this.buffer.subarray(this.offset, this.offset + size); if (b.length !== size) throw new Error("Unexpected EOF"); this.offset += size; return b; }
  u8() { return this.take(1).readUInt8(); }
  i8() { return this.take(1).readInt8(); }
  u16() { return this.take(2).readUInt16LE(); }
  i16() { return this.take(2).readInt16LE(); }
  i32() { return this.take(4).readInt32LE(); }
  f32() { return this.take(4).readFloatLE(); }
  string() { return this.take(this.i32()).toString("utf8"); }
  vec2() { return [this.f32(), this.f32()]; }
  vec3() { return [this.f32(), this.f32(), this.f32()]; }
  vec4() { return [this.f32(), this.f32(), this.f32(), this.f32()]; }
}

function verifyPmx(buffer) {
  const r = new Reader(buffer);
  if (r.take(4).toString("ascii") !== "PMX ") throw new Error("Invalid signature");
  if (Math.abs(r.f32() - 2.0) > 1e-6) throw new Error("Invalid version");
  const headerSize = r.u8();
  const globals = [...r.take(headerSize)];
  if (headerSize !== 8 || globals[0] !== 1 || globals[5] !== 2) throw new Error("Invalid global header");
  const names = [r.string(), r.string()];
  r.string(); r.string();
  const vertexCount = r.i32();
  for (let index = 0; index < vertexCount; index++) {
    r.vec3(); r.vec3(); r.vec2();
    if (r.u8() !== 0) throw new Error("Compatibility vertex is not BDEF1");
    r.i16(); r.f32();
  }
  const surfaceIndexCount = r.i32();
  r.take(surfaceIndexCount);
  if (r.i32() !== 0) throw new Error("Reference PMX contains textures");
  const materialCount = r.i32();
  for (let index = 0; index < materialCount; index++) {
    r.string(); r.string(); r.vec4(); r.vec3(); r.f32(); r.vec3(); r.u8(); r.vec4(); r.f32();
    r.i8(); r.i8(); r.u8();
    const sharedToon = r.u8();
    if (sharedToon === 0) r.i8(); else if (sharedToon === 1) r.u8(); else throw new Error("Invalid toon flag");
    r.string(); r.i32();
  }
  if (vertexCount !== 3 || surfaceIndexCount !== 3 || materialCount !== 1) throw new Error("Unexpected compatibility mesh");
  const parsedBones = [];
  const parsedIk = [];
  const count = r.i32();
  for (let index = 0; index < count; index++) {
    const name = r.string(); r.string(); r.vec3(); r.i16(); r.i32();
    const flags = r.u16();
    if (flags & FLAGS.tailIndex) r.i16(); else r.vec3();
    if (flags & (FLAGS.inheritRotation | 0x0200)) { r.i16(); r.f32(); }
    if (flags & 0x0400) r.vec3();
    if (flags & 0x0800) { r.vec3(); r.vec3(); }
    if (flags & 0x2000) r.i32();
    if (flags & FLAGS.ik) {
      const target = r.i16();
      const iterations = r.i32();
      const angle = r.f32();
      const linkCount = r.i32();
      const links = [];
      for (let link = 0; link < linkCount; link++) {
        const linkIndex = r.i16();
        const limited = r.u8() !== 0;
        if (limited) { r.vec3(); r.vec3(); }
        links.push(linkIndex);
      }
      parsedIk.push({ name, target, iterations, angle, links });
    }
    parsedBones.push(name);
  }
  const morphCount = r.i32();
  const parsedMorphs = [];
  for (let index = 0; index < morphCount; index++) {
    parsedMorphs.push(r.string()); r.string(); r.u8();
    if (r.u8() !== 1 || r.i32() !== 0) throw new Error("Morph is not an empty vertex morph");
  }
  const frameCount = r.i32();
  for (let frame = 0; frame < frameCount; frame++) {
    r.string(); r.string(); r.u8();
    const elementCount = r.i32();
    for (let element = 0; element < elementCount; element++) {
      const type = r.u8();
      if (type === 0) r.i16(); else if (type === 1) r.i8(); else throw new Error("Invalid display element");
    }
  }
  if (r.i32() !== 0 || r.i32() !== 0 || r.offset !== buffer.length) throw new Error("Unexpected physics or trailing data");
  const requiredBones = ["全ての親", "センター", "グルーブ", "腰", "下半身", "上半身", "上半身2", "首", "頭", "左足", "左ひざ", "左足首", "左つま先", "右足", "右ひざ", "右足首", "右つま先"];
  for (const name of requiredBones) if (!parsedBones.includes(name)) throw new Error(`Missing required bone: ${name}`);
  const expectedIk = ["左足ＩＫ", "左つま先ＩＫ", "右足ＩＫ", "右つま先ＩＫ"];
  if (parsedIk.map(ik => ik.name).join("|") !== expectedIk.join("|")) throw new Error("Unexpected IK definitions");
  if (!parsedMorphs.includes("まばたき") || !parsedMorphs.includes("あ")) throw new Error("Missing standard morphs");
  return { names, vertexCount, materialCount, boneCount: count, ik: parsedIk.map(value => value.name), morphCount };
}

const scriptDirectory = path.dirname(fileURLToPath(import.meta.url));
const defaultOutput = path.resolve(scriptDirectory, "../Editor/Model/_model.pmx");
const outputPath = path.resolve(process.argv[2] ?? defaultOutput);
const data = generatePmx();
const summary = verifyPmx(data);
fs.mkdirSync(path.dirname(outputPath), { recursive: true });
const temporaryPath = `${outputPath}.tmp`;
fs.writeFileSync(temporaryPath, data);
fs.renameSync(temporaryPath, outputPath);
console.log(JSON.stringify({ outputPath, bytes: data.length, ...summary }, null, 2));
