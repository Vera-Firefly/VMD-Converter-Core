# MMD Standard Humanoid Reference Rig

`_model.pmx` is a PMX 2.0 reference skeleton for VMD motion conversion. It contains:

- standard MMD humanoid bone names and positions;
- arm and hand twist chains and elbow grant relationships;
- left/right leg IK and toe IK chains;
- common MMD morph names represented by empty vertex morphs;
- a minimal compatibility triangle and material.

Generate the PMX from the package root:

```text
node Tools~/generate-reference-pmx.mjs
```

The PMX and generator use Apache License 2.0. See `LICENSE.txt` and `NOTICE.txt` in this directory.
