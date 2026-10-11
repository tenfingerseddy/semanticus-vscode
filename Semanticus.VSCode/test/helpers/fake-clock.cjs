// A fake wall clock for one test process. Preloaded with `node --require`, it moves "now" to the instant
// in FAKE_NOW_MS and leaves every other Date behaviour alone.
//
// WHY IT EXISTS. plan-page.test.mjs failed between midnight and about 02:00 local and passed the rest of
// the day, because its fixture was built off the real clock: an entry three and a half hours back was
// "today" at 14:00 and "yesterday" at 00:20, so the timeline's day groups came out [6, 3, 2] instead of
// [4, 2, 3, 2]. Running the suite at one hour proves nothing about the other twenty-three, so the fix is
// proved by running the same test at two hours that used to disagree.
//
// It replaces the Date CONSTRUCTOR called with no arguments and Date.now, which is the whole of "what
// time is it". `new Date(iso)`, `new Date(ms)`, Date.parse and Date.UTC are the real ones, so a fixture
// that names an instant still gets that instant, and `instanceof Date` still holds because the prototype
// is shared.
const at = Number(process.env.FAKE_NOW_MS);
if (!Number.isFinite(at)) throw new Error('fake-clock.cjs needs FAKE_NOW_MS set to a millisecond instant.');

const RealDate = Date;

function FakeDate(...args) {
    if (!new.target) return new RealDate(at).toString();
    return args.length === 0 ? new RealDate(at) : new RealDate(...args);
}
FakeDate.prototype = RealDate.prototype;
FakeDate.now = () => at;
FakeDate.parse = RealDate.parse;
FakeDate.UTC = RealDate.UTC;

globalThis.Date = FakeDate;
