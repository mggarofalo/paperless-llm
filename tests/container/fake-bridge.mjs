// Exercises process transport; deliberately does not use credentials or a real model.
if(process.argv[2]==='status') { console.log('{"type":"ready"}'); process.exit(0); }
if(process.argv[2]==='models') { console.log('{"type":"models","models":["gpt-6-sol"]}'); process.exit(0); }
let raw=''; for await (const chunk of process.stdin) raw+=chunk;
if(process.argv[2]!=='infer') throw Error('unexpected command');
const input=JSON.parse(raw);
if(input.images?.length !== 0 || input.reasoning !== 'low' || !input.schema || !input.instructions || !input.prompt) throw Error('missing inference input');
const response=await fetch('http://paperless:8080/test/infer');
console.log(JSON.stringify({type:'result',text:JSON.stringify(await response.json())}));
