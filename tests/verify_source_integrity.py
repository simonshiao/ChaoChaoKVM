from pathlib import Path
import re
import xml.etree.ElementTree as ET
from html.parser import HTMLParser

root = Path(__file__).resolve().parents[1]
app = root / "src/ChaoChaoKvm.App"

for path in [app / "App.xaml", app / "MainWindow.xaml", app / "ChaoChaoKvm.App.csproj"]:
    ET.parse(path)

class Parser(HTMLParser):
    pass
parser = Parser()
parser.feed((app / "Web/index.html").read_text(encoding="utf-8"))
parser.close()

html = (app / "Web/index.html").read_text(encoding="utf-8")
for asset in ("styles.css", "app.js"):
    assert asset in html
    assert (app / "Web" / asset).is_file()

main = (app / "MainWindow.xaml.cs").read_text(encoding="utf-8")
bridge = (app / "Bridge/BrowserBridge.cs").read_text(encoding="utf-8")
state = (app / "Services/AppStateService.cs").read_text(encoding="utf-8")
assert "EnsureCoreWebView2Async" in main
assert "File.Exists(indexPath)" in main
assert "app.chaokvm.local" in main and "app.chaokvm.local" in bridge
assert "port is < 1 or > 65535" in state
assert "AreDevToolsEnabled = false" in main
print("SOURCE_INTEGRITY_STATIC_PASS")
