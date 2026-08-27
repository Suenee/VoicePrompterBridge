using System.Diagnostics;
using System.Drawing;
using System.Threading;

namespace VPBridgeTray;

internal enum BridgeState { Running, Stopped, Error, Restarting }

internal sealed class SUBContext:ApplicationContext
{
 const string Version="0.8.0";
 readonly string baseDir=AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
 readonly NotifyIcon tray;
 readonly Icon icon;
 readonly ContextMenuStrip menu;
 readonly Form menuOwner;
 readonly ToolStripMenuItem stateItem,startItem,stopItem,restartItem,settingsItem,mailboxesItem,viewLogItem,exitItem;
 readonly System.Windows.Forms.Timer processTimer=new(){Interval=1000};
 readonly EventWaitHandle activationEvent;
 readonly Thread activationThread;
 Process? server;MailboxesForm? mailboxes;SUBLogForm? log;SUBSettingsForm? settings;bool exiting;Point lastMenuPoint;
 string RuntimeDir=>Path.Combine(baseDir,"runtime");string ServerExe=>Path.Combine(RuntimeDir,"SUB.Server.exe");string Script=>Path.Combine(baseDir,"dist","main.js");string Db=>Path.Combine(baseDir,"data","sub.db");string Log=>Path.Combine(baseDir,"logs","SocketUniverseBridge.log");string Status=>Path.Combine(RuntimeDir,"status.json");string Config=>Path.Combine(baseDir,"config","vpbridge.json");

 public SUBContext(EventWaitHandle showLogEvent)
 {
  activationEvent=showLogEvent;
  Directory.CreateDirectory(RuntimeDir);Directory.CreateDirectory(Path.Combine(baseDir,"logs"));
  try{icon=UiIcons.CreateAppIcon(32);}catch{icon=(Icon)SystemIcons.Application.Clone();}

  menu=new ContextMenuStrip{AutoClose=true};
  menuOwner=new Form{FormBorderStyle=FormBorderStyle.None,ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Size=new Size(1,1),Opacity=0.01,TopMost=true};
  _=menuOwner.Handle;
  menuOwner.Deactivate+=(_,_)=>{if(menu.Visible&&!menu.Bounds.Contains(Cursor.Position))menu.Close(ToolStripDropDownCloseReason.AppClicked);};
  menu.Closed+=(_,_)=>{if(menuOwner.Visible)menuOwner.Hide();};

  var titleItem=new ToolStripMenuItem($"Socket Universe Bridge v{Version}",icon.ToBitmap()){Enabled=false};
  stateItem=new ToolStripMenuItem("Stopped",UiIcons.Create(UiIconKind.Stopped,20)){Enabled=false};
  startItem=new ToolStripMenuItem("Start",UiIcons.Create(UiIconKind.Start,20),(_,_)=>{Start();ReopenMenuSoon();});
  stopItem=new ToolStripMenuItem("Stop",UiIcons.Create(UiIconKind.Stop,20),(_,_)=>{Stop("shutdown",false);ReopenMenuSoon();});
  restartItem=new ToolStripMenuItem("Restart",UiIcons.Create(UiIconKind.Restart,20),(_,_)=>{Restart();ReopenMenuSoon();});
  settingsItem=new ToolStripMenuItem("Settings...",UiIcons.Create(UiIconKind.Settings,20),(_,_)=>ShowSettings());
  mailboxesItem=new ToolStripMenuItem("Socket boxes...",UiIcons.Create(UiIconKind.Mailboxes,20),(_,_)=>ShowMailboxes());
  viewLogItem=new ToolStripMenuItem("View traffic log...",UiIcons.Create(UiIconKind.Log,20),(_,_)=>ShowLog());
  exitItem=new ToolStripMenuItem("Exit",UiIcons.Create(UiIconKind.Exit,20),(_,_)=>Exit());
  menu.Items.Add(titleItem);menu.Items.Add(new ToolStripSeparator());menu.Items.Add(stateItem);menu.Items.Add(new ToolStripSeparator());menu.Items.Add(startItem);menu.Items.Add(stopItem);menu.Items.Add(restartItem);menu.Items.Add(new ToolStripSeparator());menu.Items.Add(settingsItem);menu.Items.Add(mailboxesItem);menu.Items.Add(viewLogItem);menu.Items.Add(new ToolStripSeparator());menu.Items.Add(exitItem);

  tray=new NotifyIcon{Icon=icon,Text="Socket Universe Bridge",Visible=true};
  tray.MouseClick+=(_,e)=>{if(e.Button==MouseButtons.Left||e.Button==MouseButtons.Right)ShowTrayMenu();};
  processTimer.Tick+=(_,_)=>CheckServerProcess();processTimer.Start();

  activationThread=new Thread(ActivationLoop){IsBackground=true,Name="SUB single-instance activation"};activationThread.Start();
  SetState(BridgeState.Stopped);Start();
 }

 void ActivationLoop(){while(!exiting){try{activationEvent.WaitOne();if(exiting)break;if(!menuOwner.IsDisposed)menuOwner.BeginInvoke((Action)ShowLog);}catch{if(exiting)break;}}}
 void ShowTrayMenu(){if(menu.Visible){menu.Close(ToolStripDropDownCloseReason.AppClicked);return;}lastMenuPoint=Cursor.Position;ShowTrayMenuAt(lastMenuPoint);}
 void ShowTrayMenuAt(Point point){menuOwner.Location=point;if(!menuOwner.Visible)menuOwner.Show();menuOwner.Activate();menu.Show(point);}
 void ReopenMenuSoon(){var p=lastMenuPoint;var t=new System.Windows.Forms.Timer{Interval=90};t.Tick+=(_,_)=>{t.Stop();t.Dispose();if(!exiting&&!menu.Visible)ShowTrayMenuAt(p);};t.Start();}

 void SetState(BridgeState state,string? detail=null){string text=state switch{BridgeState.Running=>"Running",BridgeState.Error=>"Error",BridgeState.Restarting=>"Restarting",_=>"Stopped"};stateItem.Text=text;stateItem.Image?.Dispose();stateItem.Image=UiIcons.Create(state switch{BridgeState.Running=>UiIconKind.Running,BridgeState.Error=>UiIconKind.Error,BridgeState.Restarting=>UiIconKind.Restart,_=>UiIconKind.Stopped},20);bool running=state==BridgeState.Running;bool restarting=state==BridgeState.Restarting;startItem.Enabled=!running&&!restarting;stopItem.Enabled=running&&!restarting;restartItem.Enabled=!restarting;try{tray.Text=$"Socket Universe Bridge - {text}";}catch{}if(detail!=null)Debug.WriteLine(detail);}
 void Start(){if(server is{HasExited:false}){SetState(BridgeState.Running);return;}try{EnsureNode();if(!File.Exists(Script))throw new FileNotFoundException("Missing dist\\main.js. Run npm run build first.");if(!File.Exists(Config))throw new FileNotFoundException("Missing config\\vpbridge.json.");server=Process.Start(new ProcessStartInfo{FileName=ServerExe,Arguments=$"\"{Script}\"",WorkingDirectory=baseDir,UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardInput=true})??throw new InvalidOperationException("Server process could not be started.");server.StandardInput.AutoFlush=true;Thread.Sleep(300);if(server.HasExited){int code=server.ExitCode;server.Dispose();server=null;throw new InvalidOperationException($"Server stopped immediately after start. Exit code: {code}.");}SetState(BridgeState.Running);}catch(Exception e){server=null;SetState(BridgeState.Error,e.Message);tray.ShowBalloonTip(5000,"SUB ERROR",e.Message,ToolTipIcon.Error);}}
 void Stop(string reason,bool silent){try{if(server!=null){if(!server.HasExited){server.StandardInput.WriteLine(reason);server.StandardInput.Flush();if(!server.WaitForExit(1500)){server.Kill();server.WaitForExit(2000);}}server.Dispose();server=null;}if(!silent)SetState(BridgeState.Stopped);}catch(Exception e){server=null;SetState(BridgeState.Error,e.Message);if(!silent)tray.ShowBalloonTip(5000,"SUB ERROR",e.Message,ToolTipIcon.Error);}}
 void Restart(){SetState(BridgeState.Restarting);try{Stop("restart",true);Start();}catch(Exception e){SetState(BridgeState.Error,e.Message);}}
 void CheckServerProcess(){if(server==null)return;try{if(server.HasExited){int code=server.ExitCode;server.Dispose();server=null;if(!exiting)SetState(BridgeState.Error,$"Server exited unexpectedly (code {code})");}}catch{}}
 void EnsureNode(){if(File.Exists(ServerExe))return;var paths=(Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator);var node=paths.Select(p=>Path.Combine(p.Trim().Trim('"'),"node.exe")).FirstOrDefault(File.Exists)??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"nodejs","node.exe");if(!File.Exists(node))throw new InvalidOperationException("Node.js was not found.");File.Copy(node,ServerExe,true);}
 void ShowSettings(){if(settings!=null&&!settings.IsDisposed){RestoreWindow(settings);return;}settings=new SUBSettingsForm(Config,()=>Restart(),icon);settings.FormClosed+=(_,_)=>settings=null;settings.Show();settings.Activate();}
 void ShowMailboxes(){if(mailboxes!=null&&!mailboxes.IsDisposed){RestoreWindow(mailboxes);return;}mailboxes=new MailboxesForm(Db,Status,icon);mailboxes.Text="Socket boxes";mailboxes.Shown+=(_,_)=>mailboxes.BeginInvoke((Action)(()=>FitSocketBoxesHeight(mailboxes)));mailboxes.FormClosed+=(_,_)=>mailboxes=null;mailboxes.Show();mailboxes.Activate();}
 static void FitSocketBoxesHeight(Form form){if(form.IsDisposed)return;var split=form.Controls.OfType<TableLayoutPanel>().SelectMany(x=>x.Controls.OfType<SplitContainer>()).FirstOrDefault();if(split==null)return;var editor=split.Panel2.Controls.OfType<TableLayoutPanel>().FirstOrDefault();if(editor==null)return;int preferred=editor.GetPreferredSize(new Size(editor.ClientSize.Width,0)).Height;if(preferred>editor.ClientSize.Height){int grow=preferred-editor.ClientSize.Height+4;form.ClientSize=new Size(form.ClientSize.Width,form.ClientSize.Height+grow);}}
 void ShowLog(){if(log!=null&&!log.IsDisposed){RestoreWindow(log);return;}log=new SUBLogForm(Log,Db,Status,Config,icon);log.FormClosed+=(_,_)=>log=null;log.Show();log.Activate();}
 static void RestoreWindow(Form form){if(form.WindowState==FormWindowState.Minimized)form.WindowState=FormWindowState.Normal;if(!form.Visible)form.Show();form.BringToFront();form.Activate();}
 void Exit(){exiting=true;processTimer.Stop();try{activationEvent.Set();}catch{}Stop("exit",true);if(settings!=null&&!settings.IsDisposed)settings.Close();if(mailboxes!=null&&!mailboxes.IsDisposed)mailboxes.Close();if(log!=null&&!log.IsDisposed)log.Close();tray.Visible=false;tray.Dispose();menu.Dispose();menuOwner.Dispose();icon.Dispose();ExitThread();}
}

internal static class SUBProgram
{
 const string MutexName=@"Local\SocketUniverseBridge.Tray";const string EventName=@"Local\SocketUniverseBridge.ShowLog";
 [STAThread]static void Main(){using var mutex=new Mutex(true,MutexName,out bool firstInstance);if(!firstInstance){for(int i=0;i<10;i++){try{using var evt=EventWaitHandle.OpenExisting(EventName);evt.Set();return;}catch(WaitHandleCannotBeOpenedException){Thread.Sleep(50);}}return;}using var activationEvent=new EventWaitHandle(false,EventResetMode.AutoReset,EventName);ApplicationConfiguration.Initialize();try{Application.Run(new SUBContext(activationEvent));}finally{try{mutex.ReleaseMutex();}catch{}}}
}
