"""Build a consumer against the packaged targets and verify its external module root."""
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]


@unittest.skipUnless(shutil.which("dotnet"), "Requires the .NET 10 SDK")
class BrowserAssetTests(unittest.TestCase):
    def test_package_module_has_a_resolvable_content_root(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            package = root / "package"
            (package / "browser").mkdir(parents=True)
            (package / "buildTransitive").mkdir()
            module = package / "browser/broiler.hosting.webassembly.js"
            shutil.copy2(ROOT / "src/Broiler.Hosting.WebAssembly/wwwroot/broiler.hosting.webassembly.js", module)
            shutil.copy2(ROOT / "src/Broiler.Hosting.WebAssembly/buildTransitive/Broiler.Hosting.WebAssembly.targets", package / "buildTransitive/Hosting.targets")
            app = root / "app"
            (app / "wwwroot").mkdir(parents=True)
            (app / "Program.cs").write_text('System.Console.WriteLine("asset fixture");')
            (app / "wwwroot/index.html").write_text('<script type="importmap"></script>')
            (app / "App.csproj").write_text('''<Project Sdk="Microsoft.NET.Sdk.WebAssembly">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><OverrideHtmlAssetPlaceholders>true</OverrideHtmlAssetPlaceholders></PropertyGroup>
  <Import Project="../package/buildTransitive/Hosting.targets" />
</Project>''')
            for _ in range(2):
                result = subprocess.run(["dotnet", "build", str(app / "App.csproj"), "--nologo", "-v", "quiet"], capture_output=True, text=True)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                manifest = json.loads((app / "obj/Debug/net10.0/staticwebassets.build.json").read_text(encoding="utf-8"))
                assets = [a for a in manifest["Assets"] if Path(a["Identity"]).name == module.name]
                self.assertEqual(len(assets), 1)
                asset = assets[0]
                self.assertEqual(Path(asset["ContentRoot"]).resolve(), module.parent.resolve())
                self.assertEqual(asset["SourceType"], "Computed")
                self.assertEqual(Path(asset["Identity"]).read_bytes(), module.read_bytes())


if __name__ == "__main__":
    unittest.main()
