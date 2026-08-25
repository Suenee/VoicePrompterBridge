using System.Diagnostics;
using System.Drawing;

namespace VPBridgeTray;

internal sealed class SUBContext:ApplicationContext
{
 readonly string baseDir=AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar); readonly NotifyIcon tray; readonly Icon icon; Process server; MailboxesForm mailboxes; LogForm log;
 string RuntimeDir=>Path.Combine(baseDir,"runtime"); string ServerExe=>Path.Combine(RuntimeDir,"SUB.Server.exe"); string Script=>Path.Combine(baseDir,"dist","main.js"); string Db=>Path.Combine(baseDir,"data","sub.db"); string Log=>Path.Combine(baseDir,"logs","vpbridge.log"); string Status=>Path.Combine(RuntimeDir,"status.json");
 public SUBContext(){Directory.CreateDirectory(RuntimeDir);Directory.CreateDirectory(Path.Combine(baseDir,"logs"));try{icon=UiIcons.CreateAppIcon(32);}catch{icon=(Icon)SystemIcons.Application.Clone();}var menu=new ContextMenuStrip();menu.Items.Add(new ToolStripMenuItem("Socket Universe Bridge v0.8.0"){Enabled=false});menu.Items.Add(new ToolStripSeparator());menu.Items.Add("Start",null,(_,_)=>Start());menu.Items.Add("Stop",null,(_,_)=>Stop("shutdown"));menu.Items.Add("Restart",null,(_,_)=>{Stop("restart");Start();});menu.Items.Add(new ToolStripSeparator());menu.Items.Add("Mailboxes...",null,(_,_)=>ShowMailboxes());menu.Items.Add("View log",null,(_,_)=>ShowLog());menu.Items.Add(new ToolStripSeparator());menu.Items.Add("Exit",null,(_,_)=>Exit());tray=new NotifyIcon{Icon=icon,Text="Socket Universe Bridge",Visible=true,ContextMenuStrip=menu};Start();}
 void Start(){if(server is {HasExited:false})return;try{EnsureNode();if(!File.Exists(Script))throw new FileNotFoundException("Missing dist\\main.js. Run npm run build first.");server=Process.Start(new ProcessStartInfo{FileName=ServerExe,Arguments=$"\"{Script}\"",WorkingDirectory=baseDir,UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true})!;tray.Text="Socket Universe Bridge - Running";}catch(Exception e){tray.ShowBalloonTip(5000,"SUB ERROR",e.Message,ToolTipIcon.Error);}}
 void EnsureNode(){if(File.Exists(ServerExe))return;var paths=(Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator);var node=paths.Select(p=>Path.Combine(p.Trim().Trim('"'),"node.exe")).FirstOrDefault(File.Exists)??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"nodejs","node.exe");if(!File.Exists(node))throw new InvalidOperationException("Node.js was not found.");File.Copy(node,ServerExe,true);}
 void Stop(string reason){if(server==null)return;try{if(!server.HasExited){server.StandardInput.WriteLine(reason);server.StandardInput.Flush();if(!server.WaitForExit(1500)){server.Kill();server.WaitForExit(2000);}}server.Dispose();}catch{}server=null;tray.Text="Socket Universe Bridge - Stopped";}
 void ShowMailboxes(){if(mailboxes!=null&&!mailboxes.IsDisposed){mailboxes.Activate();return;}mailboxes=new MailboxesForm(Db,icon);mailboxes.FormClosed+=(_,_)=>mailboxes=null;mailboxes.Show();}
 void ShowLog(){if(log!=null&&!log.IsDisposed){log.Activate();return;}log=new LogForm(Log,Status,icon);log.FormClosed+=(_,_)=>log=null;log.Show();}
 void Exit(){Stop("exit");tray.Visible=false;tray.Dispose();icon.Dispose();ExitThread();}
}

internal static class SUBProgram
{
 [STAThread] static void Main(){ApplicationConfiguration.Initialize();Application.Run(new SUBContext());}
}
