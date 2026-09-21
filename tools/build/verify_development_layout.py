"""Exercise actual MSBuild output and restored launch profiles without opening a game world.

Runs in a temporary Unicode/space-containing checkout copy. Never installs to the user's Mods,
changes saves or deletes an existing output. Only logs, hashes and probe metadata are exported.
"""
from __future__ import annotations
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import shutil
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[2]
CLIENT = Path('src/VintageRTX.Client/VintageRTX.Client.csproj')
CORE = Path('src/VintageRTX.Core/VintageRTX.Core.csproj')
PROBE = Path('tools/build/PackageProbe/PackageProbe.csproj')


def require(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)


def run(command: list[str], cwd: Path, env: dict[str, str], log, *, success: bool = True) -> str:
    log.write('\n$ ' + json.dumps(command, ensure_ascii=False) + '\n')
    log.flush()
    process = subprocess.run(command, cwd=cwd, env=env, text=True, encoding='utf-8',
                             errors='replace', stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=180)
    log.write(process.stdout)
    log.flush()
    require((process.returncode == 0) == success,
            f"Unexpected exit {process.returncode}: {command}\n{process.stdout[-10000:]}")
    return process.stdout


def properties(project: Path, configuration: str, cwd: Path, env: dict[str, str], log,
               extra: tuple[str, ...] = ()) -> dict[str, str]:
    text = run(['dotnet', 'msbuild', str(project), '-nologo', f'-p:Configuration={configuration}',
                '-getProperty:VintageStoryPath,TargetPath,TargetDir', *extra], cwd, env, log)
    # A first-use SDK greeting may precede the property JSON, but no missing JSON is accepted.
    start = text.find('{')
    require(start >= 0, 'MSBuild did not return evaluated properties')
    return json.loads(text[start:])['Properties']


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def normalized(value: str) -> Path:
    return Path(value.replace('\\', '/')).resolve()


def package_check(source: Path, package: Path, core: Path) -> dict:
    require(package.is_dir(), f'Mod folder is missing: {package}')
    require({p.name for p in package.glob('*.dll')} == {'VintageRTX.dll', 'VintageRTX.Core.dll'},
            'Only the two mod-owned DLLs belong in the development package')
    require(digest(package / 'VintageRTX.Core.dll') == digest(core), 'The packaged core is not the current build')
    require((package / 'modinfo.json').read_bytes() == (source / 'modinfo.json').read_bytes(), 'Stale manifest')
    manifest = json.loads((package / 'modinfo.json').read_text(encoding='utf-8-sig'))
    require(manifest['modid'] == 'vintagertx' and manifest['type'] == 'code', 'Invalid mod identity')
    require(manifest['side'].lower() == 'client' and manifest['dependencies']['game'] == '1.22.7', 'Invalid target')
    assets = sorted(p for p in (source / 'assets').rglob('*') if p.is_file())
    require(bool(assets), 'No assets were checked')
    for asset in assets:
        deployed = package / asset.relative_to(source)
        require(deployed.is_file() and asset.read_bytes() == deployed.read_bytes(), f'Missing/stale asset: {asset}')
    return {'assetFiles': len(assets), 'files': {p.relative_to(package).as_posix(): digest(p)
             for p in sorted(package.rglob('*')) if p.is_file()}}


def check_profiles(source: Path, package: Path, configuration: str, game: Path) -> None:
    profiles = json.loads((source / 'Properties/launchSettings.json').read_text(encoding='utf-8'))['profiles']
    worlds = {'Vintage Story Client': 'foggy village world',
              'Vintage Story Client (quick creative)': 'creative', 'Vintage Story Client (menu)': None}
    require(set(profiles) == set(worlds), 'Historical startup profiles were lost or renamed')
    substitutions = {'VintageStoryPath': str(game), 'ProjectDir': str(source) + os.sep, 'Configuration': configuration}
    def expand(text: str) -> str:
        for key, value in substitutions.items():
            text = text.replace('$(' + key + ')', value)
        require('$(' not in text, 'Unresolved debugger property: ' + text)
        return text
    for name, profile in profiles.items():
        require(profile['commandName'] == 'Executable', name + ' must launch the game executable')
        require(normalized(expand(profile['executablePath'])) == game / 'Vintagestory.exe', 'Wrong executable')
        require(normalized(expand(profile['workingDirectory'])) == game, 'Wrong game working directory')
        arguments = expand(profile['commandLineArgs'])
        search = re.search(r'--addModPath\s+"([^"]+)"', arguments)
        origin = re.search(r'--addOrigin\s+"([^"]+)"', arguments)
        require(search is not None and normalized(search.group(1)) == package.parent, 'Launcher scans the wrong Mods directory')
        require(origin is not None and normalized(origin.group(1)) == source / 'assets', 'Wrong source asset origin')
        require('--tracelog' in arguments, 'Lost debugger trace logging')
        world = worlds[name]
        if world is None:
            require('--openWorld' not in arguments, 'Menu profile unexpectedly opens a save')
        else:
            require('--openWorld "' + world + '"' in arguments, 'Historical world parameter changed')
            require(profile.get('environmentVariables') == {
                'VINTAGERTX_AUTO_CAPTURE': '1', 'VINTAGERTX_AUTO_GAMEMODE': '2', 'VINTAGERTX_AUTO_BENCHMARK': '1',
                'VINTAGERTX_TEST_TIME_HOUR': '12', 'VINTAGERTX_TEST_CLEAR_WEATHER': '1'}, 'Lost historical profile variables')


def verify(root: Path, output: Path) -> None:
    output.mkdir(parents=True, exist_ok=True)
    report = {'success': False, 'platform': platform.platform(), 'checks': [],
              'scope': 'MSBuild layout, assets, debugger paths and assembly loading; NOT an in-game run'}
    try:
        require(bool(os.environ.get('VINTAGE_STORY')), 'Set VINTAGE_STORY to the official 1.22.7 client')
        game = normalized(os.environ['VINTAGE_STORY'])
        if not (game / 'VintagestoryAPI.dll').is_file():
            game = game.parent
        require((game / 'VintagestoryAPI.dll').is_file(), 'Missing official API')
        report['commit'] = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=root, text=True).strip()
        tracked = subprocess.check_output(['git', 'ls-files', '-z'], cwd=root).decode('utf-8').split('\0')
        with tempfile.TemporaryDirectory(prefix='VintageRTX déploiement ') as temp, (output / 'build.log').open('w', encoding='utf-8') as log:
            # Windows TEMP may use an 8.3 account name. Compare canonical paths on BOTH
            # sides rather than rejecting the correct MSBuild output under its long name.
            temporary = Path(temp).resolve()
            work = temporary / 'projet avec espaces'
            for relative in filter(None, tracked):
                file = root / relative
                if file.is_file():
                    destination = work / relative
                    destination.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copy2(file, destination)
            source = (work / CLIENT.parent).resolve()
            env = dict(os.environ, VINTAGE_STORY=str(game), DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_NOLOGO='1')
            # Property selection is tested in separate processes, never with cached MSBuild state.
            for supplied in (game, game / 'Lib', game / 'Mods'):
                local_env = dict(env, VINTAGE_STORY=str(supplied))
                resolved = properties(CLIENT, 'Debug', work, local_env, log)
                require(normalized(resolved['VintageStoryPath']) == game, f'Legacy path no longer resolves: {supplied}')
                report['checks'].append('resolve:' + supplied.name)
            invalid_env = dict(env, VINTAGE_STORY=str(temporary / 'installation absente'))
            override = properties(CLIENT, 'Debug', work, invalid_env, log, (f'-p:VintageStoryPath={game}',))
            require(normalized(override['VintageStoryPath']) == game, 'Explicit MSBuild path lost precedence')
            report['checks'].append('explicit-property-precedence')
            run(['dotnet', 'build', str(PROBE), '-c', 'Release', '--nologo'], work, env, log)
            probe = properties(PROBE, 'Release', work, env, log)['TargetPath']
            packages = {}
            for configuration in ('Debug', 'Release'):
                build_env = dict(env, VINTAGE_STORY=str(game / 'Lib')) if configuration == 'Debug' else env
                run(['dotnet', 'build', str(CLIENT), '-c', configuration, '--nologo'], work, build_env, log)
                settings = properties(CLIENT, configuration, work, build_env, log)
                package = normalized(settings['TargetDir'])
                expected = source / 'bin' / configuration / 'Mods/vintagertx'
                require(package == expected, f'SDK output is not game-discoverable: actual={package}, expected={expected}')
                core = normalized(properties(CORE, configuration, work, env, log)['TargetPath'])
                report[configuration] = package_check(source, package, core)
                check_profiles(source, package, configuration, game)
                probe_result = run(['dotnet', probe, str(package), str(game)], work, env, log)
                report[configuration]['probe'] = json.loads(probe_result)
                packages[configuration] = package
                report['checks'].append('build-profiles-and-assembly-load:' + configuration)
            # Missing Core must fail, not resolve some other copy in a test-host/probe output.
            core_file = packages['Debug'] / 'VintageRTX.Core.dll'
            hidden = core_file.with_suffix('.hidden')
            core_file.rename(hidden)
            try:
                result = run(['dotnet', probe, str(packages['Debug']), str(game)], work, env, log, success=False)
                require('Incomplete mod package: VintageRTX.Core.dll' in result, 'Missing Core was not diagnosed')
            finally:
                hidden.rename(core_file)
            report['checks'].append('reject-missing-core')
            # Generated/patch assets can retain their timestamps. A rebuild must still update bytes.
            asset = source / 'assets/vintagertx/config/emission.json'
            original, stat = asset.read_bytes(), asset.stat()
            try:
                asset.write_bytes(original + b'\n')
                os.utime(asset, ns=(stat.st_atime_ns, stat.st_mtime_ns))
                run(['dotnet', 'build', str(CLIENT), '-c', 'Debug', '--nologo'], work, env, log)
                require((packages['Debug'] / 'assets/vintagertx/config/emission.json').read_bytes() == original + b'\n', 'Same-timestamp asset stayed stale')
            finally:
                asset.write_bytes(original)
                os.utime(asset, ns=(stat.st_atime_ns, stat.st_mtime_ns))
            report['checks'].append('same-timestamp-asset-update')
            before = {p.relative_to(packages['Debug']).as_posix(): digest(p) for p in packages['Debug'].rglob('*') if p.is_file()}
            redirect = temporary / 'sortie isolée'
            run(['dotnet', 'build', str(CLIENT), '-c', 'Debug', '--artifacts-path', str(redirect), '--nologo'], work, env, log)
            isolated = properties(CLIENT, 'Debug', work, env, log, (f'-p:ArtifactsPath={redirect}',))
            isolated_package = normalized(isolated['TargetDir'])
            require(isolated_package.is_relative_to(redirect.resolve()), 'Redirected build escaped the isolation directory')
            isolated_core = normalized(properties(CORE, 'Debug', work, env, log, (f'-p:ArtifactsPath={redirect}',))['TargetPath'])
            package_check(source, isolated_package, isolated_core)
            after = {p.relative_to(packages['Debug']).as_posix(): digest(p) for p in packages['Debug'].rglob('*') if p.is_file()}
            require(before == after, 'An isolated build modified the locally deployed mod')
            report['checks'].append('artifact-isolation')
            publish = temporary / 'paquet publié'
            run(['dotnet', 'publish', str(CLIENT), '-c', 'Release', '-o', str(publish), '--nologo'], work, env, log)
            core = normalized(properties(CORE, 'Release', work, env, log)['TargetPath'])
            package_check(source, publish, core)
            run(['dotnet', probe, str(publish), str(game)], work, env, log)
            report['checks'].append('publish-and-assembly-load')
            missing = run(['dotnet', 'build', str(CLIENT), '-c', 'Debug', '--nologo'], work, invalid_env, log, success=False)
            require('Vintage Story 1.22.7 client references not found' in missing, 'Missing game path lacks an actionable diagnostic')
            report['checks'].append('reject-missing-client')
        report['success'] = True
    except Exception as exception:
        report['error'] = str(exception)
        raise
    finally:
        (output / 'development-layout.json').write_text(json.dumps(report, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
        print(json.dumps(report, ensure_ascii=False), flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=ROOT)
    parser.add_argument('--output', type=Path, default=ROOT / 'artifacts/development-layout')
    args = parser.parse_args()
    verify(args.root.resolve(), args.output.resolve())
