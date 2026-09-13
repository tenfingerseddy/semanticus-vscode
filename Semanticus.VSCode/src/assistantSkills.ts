import * as fs from 'fs';
import * as path from 'path';
import { createHash } from 'crypto';

interface PackSkill { name: string; path: string; sha256: string; }
interface InstallRecord { version: string; files: Record<string, string>; }
export interface SkillInstallResult { version: string; installed: string[]; preserved: string[]; }
const hash = (body: Buffer) => createHash('sha256').update(body).digest('hex');

/** Update only files installed by Semanticus, retaining any user changes and unrelated skills. */
export function installAssistantSkills(packRoot: string, destination: string): SkillInstallResult {
    const manifest = JSON.parse(fs.readFileSync(path.join(packRoot, 'manifest.json'), 'utf8')) as { version: string; skills: PackSkill[] };
    const recordPath = path.join(destination, '.semanticus-skills.json');
    const previous: InstallRecord = fs.existsSync(recordPath)
        ? JSON.parse(fs.readFileSync(recordPath, 'utf8')) : { version: '', files: {} };
    const result: SkillInstallResult = { version: manifest.version, installed: [], preserved: [] };
    const record: InstallRecord = { version: manifest.version, files: { ...previous.files } };
    // Read and validate all shipped bytes before changing the user's installation.
    const sources = manifest.skills.map(skill => {
        if (!/^semanticus(?:-[a-z0-9]+)*$/.test(skill.name)
            || skill.path !== `plugins/semanticus/skills/${skill.name}/SKILL.md`) throw new Error('Invalid Semanticus skill path.');
        const body = fs.readFileSync(path.join(packRoot, skill.path));
        if (hash(body) !== skill.sha256) throw new Error(`The bundled skill ${skill.name} is incomplete. Reinstall Semanticus.`);
        return { skill, body };
    });
    for (const { skill, body } of sources) {
        const folder = path.join(destination, skill.name);
        const target = path.join(folder, 'SKILL.md');
        if ((fs.existsSync(folder) && fs.lstatSync(folder).isSymbolicLink())
            || (fs.existsSync(target) && fs.lstatSync(target).isSymbolicLink())) {
            result.preserved.push(skill.name);
            continue;
        }
        if (fs.existsSync(target)) {
            const current = hash(fs.readFileSync(target));
            if (current !== skill.sha256 && current !== previous.files?.[skill.name]) {
                result.preserved.push(skill.name);
                continue;
            }
        }
        fs.mkdirSync(folder, { recursive: true });
        fs.writeFileSync(target, body);
        record.files[skill.name] = skill.sha256;
        result.installed.push(skill.name);
    }
    fs.mkdirSync(destination, { recursive: true });
    fs.writeFileSync(recordPath, JSON.stringify(record, null, 2) + '\n');
    return result;
}
