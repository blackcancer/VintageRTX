"""Compare the current liquid solver to immutable cdcc00ae; fail on any changed state.
Uses dotnet and the project's normal VINTAGE_STORY references. Generates scratch sources only
under tests/obj. The baseline is never regenerated from the candidate implementation.
"""
from pathlib import Path
import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import urllib.request
import xml.etree.ElementTree as ET

ROOT=Path(__file__).resolve().parents[2]
BASE='cdcc00ae42e9e4ddc279ac10cd5f4b72237b6ceb'
BASE_FILES={
 'src/VintageRTX/Rendering/LiquidSurfaceSimulation.cs':'ad2b4728ef4272d73024c53eb32b116e1d7446d9',
 'src/VintageRTX/assets/vintagertx/shaders/display.frag':'9398ede01bc98aef3d3f6b2ed7b2bcc6322037f3'}

def load_baseline(path):
    local=subprocess.run(['git','show',f'{BASE}:{path}'],cwd=ROOT,capture_output=True)
    if local.returncode==0:
        data=local.stdout
    else:
        request=urllib.request.Request(f'https://raw.githubusercontent.com/blackcancer/VintageRTX/{BASE}/{path}',headers={'User-Agent':'VintageRTX-performance-reference'})
        with urllib.request.urlopen(request,timeout=60) as response: data=response.read()
    sha=hashlib.sha1(b'blob '+str(len(data)).encode()+b'\0'+data).hexdigest()
    if sha!=BASE_FILES[path]: raise ValueError('Reference identity mismatch: '+path)
    return data.decode('utf-8')

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output-directory',required=True,type=Path)
    args=parser.parse_args();output=args.output_directory.resolve();output.mkdir(parents=True,exist_ok=True)
    scratch=ROOT/'tests/obj/liquid-performance';scratch.mkdir(parents=True,exist_ok=True)
    source=load_baseline('src/VintageRTX/Rendering/LiquidSurfaceSimulation.cs')
    source=source[source.index('internal sealed class LiquidSurfaceSimulation'):]
    source=re.sub(r'\bLiquidSurfaceSimulation\b','LiquidSurfaceSimulationReference',source)
    (scratch/'Reference.cs').write_text('using System.Runtime.CompilerServices;\nnamespace VintageRTX.Rendering;\n'+source,encoding='utf-8')
    shutil.copyfile(ROOT/'tools/performance/LiquidReferenceBenchmark.cs',scratch/'Program.cs')
    project=ET.Element('Project',Sdk='Microsoft.NET.Sdk');props=ET.SubElement(project,'PropertyGroup')
    for key,value in {'TargetFramework':'net10.0','OutputType':'Exe','AssemblyName':'VintageRTX.SurfaceDynamics.Test',
        'ImplicitUsings':'enable','Nullable':'enable','LangVersion':'latest'}.items(): ET.SubElement(props,key).text=value
    items=ET.SubElement(project,'ItemGroup')
    ET.SubElement(items,'ProjectReference',Include=str(ROOT/'src/VintageRTX/VintageRTX.csproj'))
    for assembly,hint in [('VintagestoryAPI','$(VintageStoryPath)/VintagestoryAPI.dll'),('OpenTK.Graphics','$(VintageStoryPath)/Lib/OpenTK.Graphics.dll'),('SkiaSharp','$(VintageStoryPath)/Lib/SkiaSharp.dll')]:
        ref=ET.SubElement(items,'Reference',Include=assembly);ET.SubElement(ref,'HintPath').text=hint;ET.SubElement(ref,'Private').text='true'
    csproj=scratch/'LiquidComparison.csproj';ET.ElementTree(project).write(csproj,encoding='unicode')
    env=os.environ.copy();env['DOTNET_TieredCompilation']='0'
    subprocess.run(['dotnet','restore',str(csproj),'--source','https://api.nuget.org/v3/index.json'],cwd=ROOT,env=env,check=True)
    subprocess.run(['dotnet','run','--project',str(csproj),'-c','Release','--no-restore','--',str(output/'liquid-performance.json')],cwd=ROOT,env=env,check=True)
    report=json.loads((output/'liquid-performance.json').read_text())
    if not report.get('bitwiseIdentical') or report.get('comparedFrames')!=2160: raise ValueError('Incomplete trajectory comparison')
    report['tieredCompilation']=False
    report['candidateCommit']=subprocess.check_output(['git','rev-parse','HEAD'],cwd=ROOT,text=True).strip()
    report['candidateFilesSha256']={p:hashlib.sha256((ROOT/p).read_bytes()).hexdigest() for p in
        ['src/VintageRTX/Rendering/LiquidSurfaceSimulation.cs','src/VintageRTX/Rendering/LiquidSurfaceTopology.cs']}
    (output/'liquid-performance.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    (output/'baseline-display.frag').write_text(load_baseline('src/VintageRTX/assets/vintagertx/shaders/display.frag'),encoding='utf-8')

if __name__=='__main__':main()
