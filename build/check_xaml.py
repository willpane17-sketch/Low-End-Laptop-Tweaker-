"""Static checks for the WPF layer (run in CI and locally; WPF cannot run on Linux build agents).
1. Every {StaticResource}/{DynamicResource} key is defined in an app dictionary or the file itself.
2. Every top-level {Binding X} in a page view resolves to a property/command on its view-model.
Exit code 1 on any problem."""
import re, sys, pathlib, xml.etree.ElementTree as ET

root = pathlib.Path(__file__).resolve().parent.parent / "src" / "Tickshift.App"
problems = []

# ---------- 1. resource keys ----------
dicts = ["Themes/Colors.Dark.xaml", "Themes/Colors.Light.xaml", "Themes/Density.Comfortable.xaml", "Themes/Density.Compact.xaml",
         "Themes/Converters.xaml", "Themes/Icons.xaml", "Themes/Styles.xaml", "Views/Shared/RecommendationTemplate.xaml"]
def keys_in(text): return set(re.findall(r'x:Key="([^"]+)"', text))
global_keys = set()
for d in dicts: global_keys |= keys_in((root / d).read_text())
for theme in ["Dark", "Light"]:
    a = keys_in((root / f"Themes/Colors.{theme}.xaml").read_text())
    b = keys_in((root / "Themes/Colors.Dark.xaml").read_text())
    if a != b: problems.append(f"Colors.{theme}.xaml keys differ from Dark: {sorted(a ^ b)}")
a = keys_in((root / "Themes/Density.Compact.xaml").read_text()); b = keys_in((root / "Themes/Density.Comfortable.xaml").read_text())
if a != b: problems.append(f"Density dictionaries differ: {sorted(a ^ b)}")

for f in list(root.rglob("*.xaml")):
    if "/obj/" in str(f) or "/bin/" in str(f): continue
    text = f.read_text()
    local = keys_in(text)
    for kind, key in re.findall(r'\{(StaticResource|DynamicResource) ([^}]+)\}', text):
        key = key.strip()
        if key.startswith("{x:Type") or key.startswith("x:Type"): continue
        if f.name == "Generic.xaml" and kind == "StaticResource":
            problems.append(f"{f.name}: Generic.xaml must use DynamicResource for app keys ({key})")
        if key not in global_keys and key not in local:
            problems.append(f"{f.relative_to(root)}: undefined resource '{key}'")

# ---------- 2. bindings vs view-models ----------
def vm_members(cls):
    members = {"IsBusy", "StatusMessage", "StatusIsError", "Title"}
    for cs in (root / "ViewModels").glob("*.cs"):
        src = cs.read_text()
        m = re.search(r'class ' + cls + r'\b[^{]*\{', src)
        if not m: continue
        # class body: until next top-level "public sealed" class declaration
        body = src[m.end():]
        nxt = re.search(r'\n(public|internal) (sealed )?(partial )?(class|record) ', body)
        body = body[: nxt.start()] if nxt else body
        members |= set(re.findall(r'public [\w<>\[\]?,. ]+? (\w+)\s*(?:\{|=>)', body))
        for f in re.findall(r'\[ObservableProperty\][^;]*? _(\w+)', body):
            members.add(f[0].upper() + f[1:])
        for name in re.findall(r'\[RelayCommand[^\]]*\]\s*(?:public |private |internal )?(?:async )?(?:Task|void)\S* (\w+)\(', body):
            n = name[:-5] if name.endswith("Async") else name
            members.add(n + "Command")
        members |= set(re.findall(r'public \w[\w<>?]* (\w+)Command \{', body))
    return members

NS = "{http://schemas.microsoft.com/winfx/2006/xaml/presentation}"
TEMPLATE_TAGS = {"DataTemplate", "ControlTemplate", "ItemsPanelTemplate", "HierarchicalDataTemplate"}
pages = {p.stem.replace("View", ""): p for p in (root / "Views").glob("*View.xaml")}
vm_for = {name: f"{name}ViewModel" for name in pages}
vm_for["Main"] = "MainViewModel"
pages["Main"] = root / "Views" / "MainWindow.xaml"

binding_re = re.compile(r'\{Binding(?:\s+Path=|\s+)?([A-Za-z_][\w.]*)?')
for name, path in pages.items():
    members = vm_members(vm_for[name])
    tree = ET.parse(path)
    def walk(el, in_template):
        tag = el.tag.replace(NS, "")
        if tag.split(".")[-1] in TEMPLATE_TAGS or tag in TEMPLATE_TAGS or tag.endswith("Resources"): in_template = True
        for attr, val in el.attrib.items():
            if "{Binding" not in val: continue
            m = binding_re.search(val)
            first = (m.group(1) or "").split(".")[0] if m else ""
            if "RelativeSource" in val:
                dm = re.search(r'Path=DataContext\.(\w+)|\{Binding DataContext\.(\w+)', val)
                target = dm and (dm.group(1) or dm.group(2))
                if target and target not in members: problems.append(f"{path.name}: DataContext.{target} not on {vm_for[name]}")
                continue
            if in_template or not first or "ElementName" in val: continue
            if first not in members: problems.append(f"{path.name}: binding '{first}' not found on {vm_for[name]}")
        for child in el: walk(child, in_template)
    walk(tree.getroot(), False)

# PageStatus binds to the page's DataContext
if problems:
    print("\n".join(sorted(set(problems))))
    sys.exit(1)
print(f"XAML checks passed ({len(pages)} views, {len(global_keys)} shared resources).")
