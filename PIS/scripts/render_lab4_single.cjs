// Local PNG rendering with mxGraph, the editor's underlying graph engine.
// Dependency: .work/mxgraph/mxClient.js (mxGraph 4.2.2); no source diagrams change.
const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require('C:/Users/user/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const root = path.resolve(__dirname, '..');

(async () => {
  const browser = await chromium.launch({headless:true, executablePath:
    'C:/Users/user/AppData/Local/ms-playwright/chromium_headless_shell-1243/chrome-headless-shell-win64/chrome-headless-shell.exe'});
  try {
    const page = await browser.newPage({viewport:{width:1500,height:2500},deviceScaleFactor:2});
    await page.setContent('<!doctype html><html><head><meta charset="utf-8"><style>html,body{margin:0;padding:0;background:white}#graph{position:relative;overflow:hidden;background:white}</style></head><body><div id="graph"></div></body></html>');
    await page.evaluate(() => {window.mxLoadResources=false;window.mxLoadStylesheets=false;});
    await page.addScriptTag({path:path.join(root,'.work','mxgraph','mxClient.js')});
    const result = await page.evaluate((xml) => {
      function UmlActor() { mxShape.call(this); }
      mxUtils.extend(UmlActor,mxShape);
      UmlActor.prototype.paintVertexShape = function(c,x,y,w,h) {
        c.ellipse(x+w/3,y,w/3,h/4);c.fillAndStroke();
        c.begin();c.moveTo(x+w/2,y+h/4);c.lineTo(x+w/2,y+h*2/3);
        c.moveTo(x,y+h*2/5);c.lineTo(x+w,y+h*2/5);
        c.moveTo(x+w/2,y+h*2/3);c.lineTo(x,y+h);
        c.moveTo(x+w/2,y+h*2/3);c.lineTo(x+w,y+h);c.stroke();
      };
      mxCellRenderer.registerShape('umlActor',UmlActor);
      const container=document.getElementById('graph');
      const graph=new mxGraph(container);
      graph.setEnabled(false);graph.setHtmlLabels(true);
      const vs=graph.getStylesheet().getDefaultVertexStyle();
      Object.assign(vs,{fillColor:'#FFFFFF',strokeColor:'#000000',fontColor:'#000000',fontFamily:'Helvetica',fontSize:12,strokeWidth:1});
      graph.getStylesheet().putCellStyle('ellipse',Object.assign({},vs,{shape:'ellipse',perimeter:mxPerimeter.EllipsePerimeter}));
      Object.assign(graph.getStylesheet().getDefaultEdgeStyle(),{strokeColor:'#000000',fontColor:'#000000',fontFamily:'Helvetica',fontSize:12,strokeWidth:1,endArrow:'classic'});
      const doc=mxUtils.parseXml(xml);
      // 'default' color values are resolved by the draw.io theme in the editor.
      // Resolve them to the same black/white light theme for the local engine.
      [...doc.getElementsByTagName('mxCell')].forEach(c => {
        c.setAttribute('style',(c.getAttribute('style')||'').replace(/(?:strokeColor|fillColor|fontColor|labelBackgroundColor)=default;/g,''));
      });
      new mxCodec(doc).decode(doc.getElementsByTagName('mxGraphModel')[0],graph.getModel());
      let b=graph.getGraphBounds();
      graph.view.setTranslate(40-b.x,40-b.y);
      b=graph.getGraphBounds();
      const width=Math.ceil(b.x+b.width+40),height=Math.ceil(b.y+b.height+40);
      container.style.width=width+'px';container.style.height=height+'px';
      const states=Object.values(graph.view.states.map);
      const text=[];
      for(const state of states) {
        if(state.cell.vertex && /^uc\d+$/.test(state.cell.id)) {
          const box=state.text?.boundingBox;
          if(box)text.push({id:state.cell.id,bounds:{x:box.x,y:box.y,width:box.width,height:box.height},node:{x:state.x,y:state.y,width:state.width,height:state.height}});
        }
      }
      const crossings=[];
      for(const state of states) {
        if(!state.cell.edge)continue;
        const route=state.shape?.node?.querySelector('path');
        if(!route)continue;
        const length=route.getTotalLength();
        for(const box of text) {
          if([state.cell.source?.id,state.cell.target?.id].includes(box.id))continue;
          const n=box.node;let hit=false;
          for(let distance=3;distance<length-3;distance+=3) {
            const p=route.getPointAtLength(distance);
            if(((p.x-n.x-n.width/2)/(n.width/2-3))**2+((p.y-n.y-n.height/2)/(n.height/2-3))**2<1){hit=true;break;}
          }
          if(hit)crossings.push({edge:state.cell.id,source:state.cell.source?.id,target:state.cell.target?.id,crosses:box.id});
        }
      }
      return {width,height,text,crossings};
    },fs.readFileSync(path.join(root,'lab4-single.drawio'),'utf8'));
    await page.setViewportSize({width:result.width,height:result.height});
    await page.screenshot({path:path.join(root,'lab4-single.png'),fullPage:true});
    fs.writeFileSync(path.join(root,'.work','lab4-single-render.json'),JSON.stringify(result,null,2));
    const verificationPath=path.join(root,'lab4-single-verification.json');
    const verification=JSON.parse(fs.readFileSync(verificationPath,'utf8'));
    verification.rendering={engine:'mxGraph 4.2.2 in Chromium; UML actor reproduced for preview',
      labels:result.text.length,unrelated_oval_crossings:result.crossings.length,
      png:'lab4-single.png',width:result.width*2,height:result.height*2};
    fs.writeFileSync(verificationPath,JSON.stringify(verification,null,2)+'\n');
    console.log(JSON.stringify({png:'lab4-single.png',width:result.width*2,height:result.height*2,labels:result.text.length,crossings:result.crossings}));
  } finally {await browser.close();}
})().catch(error=>{console.error(error);process.exit(1);});
