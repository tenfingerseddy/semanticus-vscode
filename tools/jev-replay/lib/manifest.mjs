// The bars, committed before a run.
//
// manifest.json declares, for every decision point: a precision floor, a coverage floor, a
// false-positive ceiling, a false-refusal ceiling, the confidence threshold and the fallback
// taken below it. A point with no bar is REFUSED rather than scored, because a number with no
// bar beside it is a number nobody can fail, and a gate nobody can fail is not a gate.
//
// The manifest carries its own digest and a committedUtc. loadManifest recomputes the digest and
// refuses a mismatch, so an edit made after the commit cannot pass as the committed bar. Every
// gate run records the digest it ran under, so a report can be tied back to the exact bars.

import { readFileSync } from 'node:fs';
import { canonicalJson, sha256Hex } from './digest.mjs';

export class ManifestError extends Error {
  constructor(message) {
    super(message);
    this.name = 'ManifestError';
  }
}

/**
 * A manifest is "proposed" until the numbers in it are ratified, and "sealed" once they are.
 * A gate run refuses proposed bars. A number the lane wrote for itself and then measured itself
 * against is development evidence, whatever the run is called, and only ratification moves it.
 */
export const MANIFEST_STATUSES = ['proposed', 'sealed'];

export function isRatified(manifest) {
  return manifest?.status === 'sealed';
}

export const BAR_FIELDS = [
  'precisionFloor',
  'coverageFloor',
  'falsePositiveCeiling',
  'falseRefusalCeiling',
  'confidenceThreshold',
  'fallback',
];

/** The digest is taken over the manifest with the recorded digest field removed. */
export function computeManifestDigest(manifest) {
  const body = { ...manifest };
  delete body.manifestDigest;
  return sha256Hex(canonicalJson(body));
}

export function validateManifest(manifest) {
  if (typeof manifest?.committedUtc !== 'string' || manifest.committedUtc === '') {
    throw new ManifestError('manifest.json must record a committedUtc');
  }
  if (typeof manifest?.manifestDigest !== 'string' || manifest.manifestDigest === '') {
    throw new ManifestError('manifest.json must record a manifestDigest');
  }
  if (!MANIFEST_STATUSES.includes(manifest?.status)) {
    throw new ManifestError(
      `manifest.json must record a status of ${MANIFEST_STATUSES.join(' or ')}, got `
      + `"${manifest?.status}". A file that does not say whether its bars are ratified cannot `
      + 'be read either way.');
  }
  if (manifest.points === null || typeof manifest.points !== 'object') {
    throw new ManifestError('manifest.json must carry a points object');
  }
  for (const [pointId, bar] of Object.entries(manifest.points)) {
    for (const field of BAR_FIELDS) {
      if (!(field in bar)) {
        throw new ManifestError(`point "${pointId}" has no ${field} in manifest.json`);
      }
    }
  }
  const recomputed = computeManifestDigest(manifest);
  if (recomputed !== manifest.manifestDigest) {
    throw new ManifestError(
      `manifest.json digest does not match its contents. Recorded ${manifest.manifestDigest}, `
      + `recomputed ${recomputed}. The bars were changed after they were committed.`);
  }
  return true;
}

export function loadManifest(path) {
  const manifest = JSON.parse(readFileSync(path, 'utf8'));
  validateManifest(manifest);
  return manifest;
}

/** Refuses, naming the point, rather than scoring a point with no committed bar. */
export function barFor(manifest, pointId) {
  const bar = manifest.points?.[pointId];
  if (bar === undefined) {
    throw new ManifestError(
      `no bar in manifest.json for decision point "${pointId}". Bars are numeric and committed `
      + 'before the run, so this point cannot be scored until a bar is committed for it.');
  }
  return bar;
}
