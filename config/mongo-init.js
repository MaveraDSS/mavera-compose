// Creates the application users and the per-service databases.
//
// The user MUST live in `admin`: the rendered connection string is
//   mongodb://<user>:<pass>@mongo:27017/?directConnection=true&tls=false&authMechanism=SCRAM-SHA-256
// which has no database in its path, so the driver authenticates against its
// default authSource of `admin`. A user created inside each application
// database would fail with "Command saslStart failed: Authentication failed".
//
// Mongo creates databases lazily, so a marker document is inserted to
// materialise each one and make it visible in tooling straight away. That is
// not cosmetic any more: `listDatabases` only returns databases that exist, and
// it is what a project user's `show dbs` is answered from.
//
// PER-PROJECT DATABASES. MONGO_PROJECTS is a comma-separated list, the same
// shape as RABBITMQ_VHOSTS and SQL_PROJECTS. Two entry shapes:
//
//   prefix                 the 11 databases are created; the shared user below
//                          is granted readWrite on them. Conventional boundary.
//   prefix:user:password   a dedicated user is created with readWrite on THOSE
//                          11 and nothing else.
//
// Field 1 is the prefix verbatim -- the identical string the app stack puts in
// DB_PREFIX, which also prefixes its SQL catalogs. Convention is "<project>-".
//
// What a project user can and cannot do. Its roles are exactly
// [{readWrite, <prefix><name>} x 11]: never readWriteAnyDatabase, never a role
// on `admin`, never the cluster `listDatabases` action. Since MongoDB 4.0.5
// listDatabases filters to the databases the caller holds privileges on, so a
// project user cannot even learn another project's database *names*. It also
// cannot dropDatabase, read `admin`, enumerate users, or run serverStatus.
//
// USERNAMES ARE GLOBAL. Every user lives in `admin`, so two projects cannot
// share one: createUser/updateUser would silently replace the earlier project's
// role set, which is an isolation break rather than a typo. Rejected below,
// along with any name equal to the shared user.
//
// Everything is validated BEFORE anything is written, and a validation failure
// exits 2 so the compose entrypoint can fail the deploy rather than provision
// half of it.

const MAX_DB = 63;                        // Mongo: a database name is < 64 chars
const MAX_PREFIX = 38;                    // see checkPrefix
const PREFIX_RE = /^[a-z0-9][a-z0-9_-]*$/;

const databases = [
  'audit-logs',
  'evaluation-service',
  'document-processor',
  'document-classifier',
  'document-summaries',
  'document-anonymize',
  'relevance-score',
  'journalevents-classifier',
  'identity-check',
  'fkassan',
  'enhanced-patient-view',
];

const sharedUser = process.env.MONGO_APP_USER || 'mavera';
const sharedPass = process.env.MONGO_APP_PASS || 'mavera';

// Only the all-in-one docker-compose.yml sets this: one project, so the shared
// user's own databases follow DB_PREFIX. The split infra stack deliberately
// does not, because DEPLOY.md has operators paste one .env into both stacks and
// one project's prefix must not move the shared user's databases.
const sharedPrefix = (process.env.MONGO_DB_PREFIX || '').trim();

const admin = db.getSiblingDB('admin');

function fail(msg) {
  print('mongo-init: ' + msg);
  quit(2);                                // 2 = bad input, nothing was touched
}

// --- parsing -----------------------------------------------------------------

// Split on the first two colons only, so a password containing ':' survives --
// same rule as rabbitmq-init.sh and mssql-projects.sh.
function parseProjects(raw) {
  if (!raw) return [];
  return raw.split(',').map(function (s) { return s.trim(); })
            .filter(function (s) { return s.length > 0; })
            .map(function (entry) {
    const i = entry.indexOf(':');
    if (i === -1) return { entry: entry, prefix: entry, user: '', pass: '' };
    const prefix = entry.slice(0, i).trim();
    const rest = entry.slice(i + 1);
    const j = rest.indexOf(':');
    if (j === -1) return { entry: entry, prefix: prefix, user: rest.trim(), pass: '' };
    return {
      entry: entry,
      prefix: prefix,
      user: rest.slice(0, j).trim(),
      pass: rest.slice(j + 1).trim(),
    };
  });
}

// The prefix charset is shared with mssql-projects.sh, because one DB_PREFIX
// feeds both engines. The ceiling is Mongo's: 63 - 24 for the longest name,
// `journalevents-classifier`, less a character of headroom. Getting this wrong
// would break only the LONGEST few databases of a project -- the worst possible
// failure shape -- so every name is length-checked individually as well.
function checkPrefix(prefix, entry) {
  if (prefix === '') return;              // the unprefixed set
  if (!PREFIX_RE.test(prefix)) {
    fail("prefix '" + prefix + "' in entry '" + entry + "' must be lowercase " +
         'a-z 0-9 _ - and start with a letter or digit');
  }
  if (prefix.length > MAX_PREFIX) {
    fail("prefix '" + prefix + "' is " + prefix.length + ' characters; the ' +
         'limit is ' + MAX_PREFIX);
  }
}

function namesFor(prefix) {
  return databases.map(function (d) {
    const n = prefix + d;
    if (n.length > MAX_DB) {
      fail("database name '" + n + "' is " + n.length + ' characters; Mongo ' +
           'allows ' + MAX_DB + '. Shorten the prefix.');
    }
    return n;
  });
}

function rolesFor(names) {
  return names.map(function (n) { return { role: 'readWrite', db: n }; });
}

// --- writes ------------------------------------------------------------------

function materialise(names) {
  for (const n of names) {
    db.getSiblingDB(n).getCollection('_bootstrap')
      .updateOne({ _id: 'init' }, { $set: { at: new Date(), by: 'mongo-init.js' } },
                 { upsert: true });
  }
}

// Drop any stray per-database user of the same name left by an earlier run, so
// the only credential is the admin-scoped one.
function dropStalePerDbUsers(user, names) {
  for (const n of names) {
    try {
      if (db.getSiblingDB(n).getUser(user)) {
        db.getSiblingDB(n).dropUser(user);
        print('removed stale per-database user ' + user + ' from: ' + n);
      }
    } catch (e) { /* no such user */ }
  }
}

// updateUser REPLACES the whole role array, so every caller must pass the
// complete set it wants the user to end up with.
function ensureUser(user, pass, roles) {
  if (admin.getUser(user)) {
    admin.updateUser(user, { pwd: pass, roles: roles });
    print('updated admin user: ' + user + ' (' + roles.length + ' databases)');
  } else {
    admin.createUser({ user: user, pwd: pass, roles: roles });
    print('created admin user: ' + user + ' (' + roles.length + ' databases)');
  }
}

// --- validate everything before writing anything ------------------------------

const projects = parseProjects((process.env.MONGO_PROJECTS || '').trim());

checkPrefix(sharedPrefix, 'MONGO_DB_PREFIX');
const sharedNames = namesFor(sharedPrefix);

const seenPrefixes = {};
const seenUsers = {};

for (const p of projects) {
  checkPrefix(p.prefix, p.entry);
  namesFor(p.prefix);                     // length check, result discarded

  const key = p.prefix === '' ? '<empty>' : p.prefix;
  if (seenPrefixes[key]) {
    fail("prefix '" + key + "' appears twice; the second pass would just " +
         "re-provision the first project's databases");
  }
  seenPrefixes[key] = true;

  if (!p.user) continue;
  if (!p.pass) fail("'" + p.entry + "' names a user with no password");
  if (p.user === sharedUser) {
    fail("'" + p.user + "' is MONGO_USER; use the bare 'prefix' form to grant " +
         'it this project instead of redefining it');
  }
  if (seenUsers[p.user]) {
    fail("user '" + p.user + "' is used by more than one project; every user " +
         "lives in `admin`, so the second would replace the first's roles");
  }
  seenUsers[p.user] = true;
}

// --- provision ----------------------------------------------------------------

// Accumulated, then written once: a bare-prefix entry grants the shared user a
// project's databases, and updateUser replaces the whole array.
let sharedRoles = rolesFor(sharedNames);

for (const p of projects) {
  const names = namesFor(p.prefix);
  materialise(names);

  if (!p.user) {
    sharedRoles = sharedRoles.concat(rolesFor(names));
    print('project ' + (p.prefix || '<unprefixed>') + ' -> DB_PREFIX=' + p.prefix +
          ' (' + names.length + ' databases, shared user ' + sharedUser + ')');
    continue;
  }

  dropStalePerDbUsers(p.user, names);
  ensureUser(p.user, p.pass, rolesFor(names));
  print('project ' + (p.prefix || '<unprefixed>') + ' -> DB_PREFIX=' + p.prefix +
        ' (' + names.length + ' databases, user ' + p.user + ')');
}

// The shared user stays, and keeps its own databases, even when MONGO_PROJECTS
// is set -- the same way rabbitmq-init leaves the broker admin alone. Retiring
// it once every project has its own credentials is a deliberate, manual
// db.getSiblingDB('admin').dropUser('<user>').
dropStalePerDbUsers(sharedUser, sharedNames);
materialise(sharedNames);
ensureUser(sharedUser, sharedPass, sharedRoles);

for (const n of sharedNames) print('initialised database: ' + n);
