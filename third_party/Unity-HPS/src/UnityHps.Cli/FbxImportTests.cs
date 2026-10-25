using UnityHps;

internal static class FbxImportTests
{
    public static void Run(Action<bool, string> check)
    {
        check(HumanBones.Guess("Toe.L") == 19 && HumanBones.Guess("Toe.R") == 20,
            "FBX toe aliases preserve left/right Humanoid mapping without metadata");
        string folder = Path.Combine(Path.GetTempPath(), "hps-fbx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "fixture.fbx");
            File.WriteAllText(path, """
                ; FBX 7.4.0 project file
                FBXHeaderExtension: {
                    FBXHeaderVersion: 1003
                    FBXVersion: 7400
                }
                Objects: {
                    Model: 1, "Model::Hips", "LimbNode" {
                        Version: 232
                        Properties70: {
                            P: "Lcl Translation", "Lcl Translation", "", "A",0,1,0
                        }
                    }
                }
                Connections: {
                    C: "OO",1,0
                }
                """);
            File.WriteAllText(path + ".meta", """
                ModelImporter:
                  humanDescription:
                    human:
                    - boneName: Hips
                      humanName: Hips
                      limit:
                        modified: 1
                """);
            var warnings = new List<string>();
            FbxDocument doc = FbxReader.Read(path, new FbxImportOptions { Warning = warnings.Add });
            check(doc.usedUnityMeta && doc.bones.Any(b => b.humanBone == 0) &&
                warnings.Count == 1 && warnings[0].Contains("default profile limits"),
                "custom FBX muscle settings warn once and retain the imported skeleton instead of terminating");
            warnings.Clear();
            FbxReader.Read(path, new FbxImportOptions { IgnoreMeta = true, Warning = warnings.Add });
            check(warnings.Count == 0, "ignored FBX metadata does not emit a custom-limit warning");
        }
        finally
        {
            File.Delete(Path.Combine(folder, "fixture.fbx.meta"));
            File.Delete(Path.Combine(folder, "fixture.fbx"));
            Directory.Delete(folder);
        }
    }
}
