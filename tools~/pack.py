"""Build a reproducible UPM tarball, with stable Unity asset GUIDs and a checksum."""
import argparse
import gzip
import hashlib
import io
import json
from pathlib import Path
import tarfile
import uuid

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--output', type=Path, help='Output directory (default: dist~ in this SDK checkout)')
args = parser.parse_args()
output = (args.output or root / 'dist~').resolve()
manifest = json.loads((root / 'package.json').read_text())

# Unity tracks package assets by GUID. Preserve each GUID across all future packs.
for path in sorted(root.rglob('*')):
    relative = path.relative_to(root)
    if any(part.endswith('~') or part.startswith('.') for part in relative.parts) or path.suffix == '.meta':
        continue
    meta = Path(str(path) + '.meta')
    if meta.exists():
        continue
    guid = uuid.uuid5(uuid.NAMESPACE_URL, 'https://pubb.io/unity/' + relative.as_posix()).hex
    if path.is_dir():
        body = 'folderAsset: yes\nDefaultImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n'
    elif path.suffix == '.cs':
        body = 'MonoImporter:\n  externalObjects: {}\n  serializedVersion: 2\n  defaultReferences: []\n  executionOrder: 0\n  icon: {instanceID: 0}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n'
    elif path.suffix == '.asmdef':
        body = 'AssemblyDefinitionImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n'
    elif path.suffix == '.jslib':
        body = 'PluginImporter:\n  externalObjects: {}\n  serializedVersion: 2\n  isPreloaded: 0\n  isOverridable: 0\n  isExplicitlyReferenced: 0\n  validateReferences: 1\n  platformData:\n  - first:\n      Any: \n    second:\n      enabled: 0\n      settings: {}\n  - first:\n      WebGL: WebGL\n    second:\n      enabled: 1\n      settings: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n'
    else:
        body = 'DefaultImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n'
    meta.write_text('fileFormatVersion: 2\nguid: ' + guid + '\n' + '\n'.join(line.rstrip() for line in body.splitlines()) + '\n')

output.mkdir(parents=True, exist_ok=True)
archive = output / f"{manifest['name']}-{manifest['version']}.tgz"
buffer = io.BytesIO()
with tarfile.open(fileobj=buffer, mode='w', format=tarfile.PAX_FORMAT) as tar:
    for path in sorted(root.rglob('*')):
        if not path.is_file():
            continue
        rel = path.relative_to(root)
        if any(part.startswith('.') for part in rel.parts):
            continue
        if len(rel.parts) > 1 and rel.parts[0] not in ('Runtime', 'Samples~', 'Tests'):
            continue
        if len(rel.parts) == 1 and path.name not in ('package.json', 'README.md', 'CHANGELOG.md', 'LICENSE.md') and path.suffix != '.meta':
            continue
        data = path.read_bytes()
        info = tarfile.TarInfo('package/' + rel.as_posix())
        info.size = len(data)
        info.mode = 0o644
        info.mtime = 0
        tar.addfile(info, io.BytesIO(data))
with archive.open('wb') as handle:
    with gzip.GzipFile(filename='', mode='wb', fileobj=handle, mtime=0) as zipped:
        zipped.write(buffer.getvalue())
digest = hashlib.sha256(archive.read_bytes()).hexdigest()
Path(str(archive) + '.sha256').write_text(f'{digest}  {archive.name}\n')
print(f'{archive} ({archive.stat().st_size} bytes)')
