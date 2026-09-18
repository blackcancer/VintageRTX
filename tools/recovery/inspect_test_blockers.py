"""Diagnose genuine fixture bytes and summarize MSTest without dumping an entire shader."""
from pathlib import Path
import hashlib
import json
import os
import struct
import xml.etree.ElementTree as ET
import zlib

root = Path(__file__).resolve().parents[2]
fixture = root / 'tests/VintageRTX.Test/Fixtures/entity-mirror-local-body-real.png'
if fixture.exists():
    data = fixture.read_bytes()
    print('REAL_FIXTURE', json.dumps({'bytes':len(data),'sha256':hashlib.sha256(data).hexdigest(),'signature':data[:32].hex()}))
    try:
        if data[:8] != b'\x89PNG\r\n\x1a\n': raise ValueError('Not a binary PNG signature')
        at = 8
        idat = b''
        while at < len(data):
            length = struct.unpack_from('>I', data, at)[0]
            kind = data[at+4:at+8]
            chunk = data[at+8:at+8+length]
            crc = struct.unpack_from('>I', data, at+8+length)[0]
            if zlib.crc32(kind+chunk) & 0xffffffff != crc: raise ValueError('CRC mismatch: ' + repr(kind))
            if kind == b'IHDR': print('REAL_FIXTURE_IHDR', chunk.hex())
            if kind == b'IDAT': idat += chunk
            at += length + 12
            if kind == b'IEND': break
        decoded = zlib.decompress(idat)
        print('REAL_FIXTURE_PNG_STRUCTURE_OK', len(decoded), 'inflated bytes;', len(data)-at, 'trailing bytes')
    except Exception as error:
        print('REAL_FIXTURE_INVALID', str(error))
else:
    print('REAL_FIXTURE_MISSING')

results = Path(os.environ.get('BLOCKER_RESULTS', '/tmp/no-results'))
for trx in results.glob('*.trx'):
    doc = ET.parse(trx)
    ns = {'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
    counters = doc.find('.//t:Counters', ns)
    print('MSTEST_COUNTERS', json.dumps(counters.attrib if counters is not None else {}))
    for result in doc.findall('.//t:UnitTestResult', ns):
        if result.get('outcome') != 'Passed':
            message = result.find('.//t:Message', ns)
            stack = result.find('.//t:StackTrace', ns)
            print('MSTEST_RESULT', json.dumps({'test':result.get('testName'),'outcome':result.get('outcome'),
                'message':(message.text or '')[:900] if message is not None else '',
                'stack':(stack.text or '')[:1800] if stack is not None else ''}))

# Exact relevant production/test methods for a follow-up failure, without guessed line ranges.
for path, marker in [
 ('src/VintageRTX/Testing/RuntimeScenarioProbe.cs','private bool TryApplyExteriorRoofCamera('),
 ('tests/VintageRTX.Test/RuntimeCoverageProbeWorldTests.cs','private static RuntimeCoverageProbeHarness CreateExteriorHarness('),
 ('tests/VintageRTX.Test/RuntimeImageValidatorReflectionWitnessTests.cs','public void RealRuntimeLocalBodyCarrierRetainsMeasuredComponents(')]:
    text=(root/path).read_text(encoding='utf-8')
    start=text.find(marker)
    if start<0:
        print('METHOD_NOT_FOUND', path, marker)
        continue
    opening=text.index('{',start); end=opening+1; depth=1
    while depth:
        depth+=(text[end]=='{')-(text[end]=='}'); end+=1
    print('SOURCE_METHOD',path, 'line', text[:start].count('\n')+1)
    print(text[start:end][:16000])
