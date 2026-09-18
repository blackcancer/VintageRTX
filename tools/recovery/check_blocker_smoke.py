"""Require an executed blocker-smoke campaign, not an empty or skipped green run.
This validates the runner report only; it is not visual/game acceptance or a signed attestation.
"""
from pathlib import Path
import json
import sys
import xml.etree.ElementTree as ET

NAMESPACE = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
REQUIRED = {
    'SingularObliqueCandidateIsRejectedAndOrdinaryPairRemainsUsable',
    'ObliquePerspectiveReconstructsDepthWithItsOwnInverse',
    'InvalidProjectionClearsBothOutputsAndNeverMutatesSource',
    'FloatRoundingCannotPublishSingularOrInfiniteInverse',
    'ClientBootstrapRegistersAndExecutesEveryRenderingCommand',
    'DisposeClearsEmptyAndPartiallyInitializedSystemState',
    'PreFinalDisplayPassUsesRealHiddenContextAndRestoresState',
    'ExteriorRoofCameraCoversLowSunNoCandidateAndOpenGroundSuccess',
    'RealRuntimeLocalBodyCarrierRetainsMeasuredComponents',
    'RealFixtureSurvivesUnicodePathAndRejectsChangedBytes',
    'VoxelShadowTraversalAdvancesEveryTiedAxis',
    'ShaderUsesNativeVegetationLocallyAndVoxelVisibilityBeyondCascadeExit',
}


def check_report(path: Path) -> dict[str, int]:
    """Reject malformed, incomplete, failed or skipped smoke reports."""
    document = ET.parse(path)
    counters = document.find('.//t:Counters', NAMESPACE)
    results = document.findall('.//t:UnitTestResult', NAMESPACE)
    summary = document.find('.//t:ResultSummary', NAMESPACE)
    if counters is None or summary is None:
        raise ValueError('Missing MSTest counters or result summary')
    counts = {key: int(value) for key, value in counters.attrib.items()}
    total = counts.get('total', 0)
    if total < 35 or total != len(results):
        raise ValueError(f'Incomplete blocker campaign: counters={total}, rows={len(results)}')
    if counts.get('executed') != total or counts.get('passed') != total:
        raise ValueError('Every selected smoke row must execute and pass')
    if any(counts.get(key, 0) != 0 for key in
           ('failed', 'error', 'timeout', 'aborted', 'inconclusive', 'notExecuted', 'notRunnable', 'passedButRunAborted')):
        raise ValueError('MSTest reported non-passing or incomplete execution')
    if summary.get('outcome') != 'Completed' or any(row.get('outcome') != 'Passed' for row in results):
        raise ValueError('Run or individual row outcome is not successful')
    names = {row.get('testName', '') for row in results}
    missing = REQUIRED - names
    if missing or not any(name.startswith('PreflightContract (') for name in names):
        raise ValueError('Missing required smoke tests: ' + ', '.join(sorted(missing)))
    return {'total': total, 'executed': total, 'passed': total, 'failed': 0, 'skipped': 0}


if __name__ == '__main__':
    if len(sys.argv) != 2:
        raise SystemExit('Usage: python check_blocker_smoke.py path/to/blocker-smoke.trx')
    print(json.dumps(check_report(Path(sys.argv[1])), sort_keys=True))
