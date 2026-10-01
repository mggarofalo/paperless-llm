import http from 'node:http';
const keep = { action: 'keep', value: null, evidence: [] };
export const intent = { schema_version:'1', title:{action:'set',value:'Synthetic receipt',evidence:['Page 1 synthetic receipt']}, date:keep, correspondent:keep, document_type:keep, add_tags:[{id:3,evidence:['Synthetic fixture category']}], ocr:{action:'keep',pages:[],evidence:[]}, uncertainty:[] };
let document = {id:1,title:'Untitled',content:'Synthetic receipt',created:'2026-10-01',modified:'2026-10-01T00:00:00Z',correspondent:null,document_type:null,tags:[1],mime_type:'image/png',original_file_name:'synthetic.png'};
let patches=0,inferences=0,disconnect=false,tagVisible=true;
const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII=','base64');
const server=http.createServer(async(req,res)=>{
  let raw=''; for await(const chunk of req) raw+=chunk;
  const url=new URL(req.url,'http://paperless:8080');
  const json=(value,status=200)=>{res.writeHead(status,{'Content-Type':'application/json'});res.end(JSON.stringify(value));};
  if(url.pathname==='/test/ready') return json({ready:true});
  if(url.pathname==='/test/hide-tag') {tagVisible=false;return json({});}
  if(url.pathname==='/test/show-tag') {tagVisible=true;return json({});}
  if(url.pathname==='/test/infer') {inferences++; return json(intent);}
  if(url.pathname==='/test/clear') {document.tags=document.tags.filter(x=>x!==2); document.modified='2026-10-02T00:00:00Z';return json({});}
  if(url.pathname==='/test/disconnect') {disconnect=true;return json({});}
  if(url.pathname==='/test/state') return json({document,patches,inferences});
  if(req.headers.authorization!=='Token synthetic-only') return json({},401);
  if(url.pathname==='/api/documents/1/download/') {res.writeHead(200,{'Content-Type':'image/png','Content-Length':png.length});return res.end(png);}
  if(url.pathname==='/api/documents/1/' && req.method==='PATCH') {
    const patch=JSON.parse(raw); if(Object.keys(patch).some(x=>!['title','tags'].includes(x))) return json({error:'unexpected fields'},400);
    document={...document,...patch,modified:'2026-10-01T01:00:00Z'};patches++;
    if(disconnect) {disconnect=false;req.socket.destroy();return;}
    return json(document);
  }
  if(url.pathname==='/api/documents/1/') return json(document);
  let rows;
  if(url.pathname==='/api/documents/') rows=Number(url.searchParams.get('id__gt')||0)<1?[document]:[];
  else if(url.pathname==='/api/tags/') rows=tagVisible?[{id:3,name:'receipt'},{id:1,name:'inbox',is_inbox_tag:true},{id:2,name:'needs review'}]:[];
  else if(['/api/correspondents/','/api/document_types/'].includes(url.pathname)) rows=[];
  else return json({error:'unexpected route'},404);
  return json({count:rows.length,next:null,previous:null,results:rows});
});
server.listen(8080,'0.0.0.0');
