// The player only loads what ScriptingAssemblies.json lists, so the port's assembly has to be added.
const fs = require('fs');
const [file, name] = process.argv.slice(2);
const j = JSON.parse(fs.readFileSync(file, 'utf8'));
if (!j.names.includes(name)) {
  j.names.push(name);
  j.types.push(16);
}
fs.writeFileSync(file, JSON.stringify(j));
console.log(`${file}: ${j.names.length} assemblies`);
