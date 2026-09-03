// Creates the application user and the per-service databases.
//
// The user MUST live in `admin`: the rendered connection string is
//   mongodb://<user>:<pass>@mongo:27017/?directConnection=true&tls=false&authMechanism=SCRAM-SHA-256
// which has no database in its path, so the driver authenticates against its
// default authSource of `admin`. A user created inside each application
// database would fail with "Command saslStart failed: Authentication failed".
//
// Mongo creates databases lazily, so a marker document is inserted to
// materialise each one and make it visible in tooling straight away.

const user = process.env.MONGO_APP_USER || 'mavera';
const pass = process.env.MONGO_APP_PASS || 'mavera';

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

const admin = db.getSiblingDB('admin');

// Drop any stray per-database user of the same name left by an earlier run,
// so the only credential is the admin-scoped one below.
for (const dbName of databases) {
  try {
    if (db.getSiblingDB(dbName).getUser(user)) {
      db.getSiblingDB(dbName).dropUser(user);
      print('removed stale per-database user from: ' + dbName);
    }
  } catch (e) { /* no such user */ }
}

const roles = databases.map((d) => ({ role: 'readWrite', db: d }));

if (admin.getUser(user)) {
  admin.updateUser(user, { pwd: pass, roles: roles });
  print('updated admin user: ' + user);
} else {
  admin.createUser({ user: user, pwd: pass, roles: roles });
  print('created admin user: ' + user);
}

for (const dbName of databases) {
  db.getSiblingDB(dbName).getCollection('_bootstrap')
    .updateOne({ _id: 'init' }, { $set: { at: new Date(), by: 'mongo-init.js' } }, { upsert: true });
  print('initialised database: ' + dbName);
}
