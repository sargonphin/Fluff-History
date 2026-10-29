// Compile the actual page implementation into a native-only isolated test host. Its internal
// activation seam substitutes bounded fake pipe peers; the shipped DLL has no diagnostic IPC.
#include "PropertySheet.cpp"
#include <thread>
#include <functional>
#include <iostream>
#include <algorithm>
#include <source_location>
#include <chrono>

namespace {
fh::Handle stop;
std::vector<std::thread> servers;
std::atomic<int> mode{},connected{};
std::atomic<unsigned> lastAction{};std::wstring lastToken;
HRESULT TestActivate(const std::wstring& arguments){
 return fh::Boundary([&](){auto token=arguments.substr(arguments.size()-32);fh::Check(fh::Token(token));auto name=L"\\\\.\\pipe\\FluffHistory.Shell.v1."+token;
  auto pipe=CreateNamedPipeW(name.c_str(),PIPE_ACCESS_DUPLEX|FILE_FLAG_OVERLAPPED,PIPE_TYPE_BYTE|PIPE_REJECT_REMOTE_CLIENTS,1,4096,4096,0,nullptr);fh::Check(pipe!=INVALID_HANDLE_VALUE);auto behavior=mode.load();
  servers.emplace_back([pipe,behavior](){fh::Handle server(pipe);try{
   fh::Handle event(CreateEventW(nullptr,TRUE,FALSE,nullptr));OVERLAPPED op{};op.hEvent=event.value;BOOL result=ConnectNamedPipe(pipe,&op);auto error=result?ERROR_SUCCESS:GetLastError();
   if(!result&&error==ERROR_IO_PENDING){HANDLE waits[]={stop.value,event.value};if(WaitForMultipleObjects(2,waits,FALSE,3000)!=WAIT_OBJECT_0+1){CancelIoEx(pipe,&op);DWORD ignored{};GetOverlappedResult(pipe,&op,&ignored,TRUE);return;}}
   else fh::Check(result||error==ERROR_PIPE_CONNECTED);
   ReadFrame(pipe,stop.value,3000);++connected;
   if(behavior==3){WaitForSingleObject(stop.value,10000);return;}
   if(behavior==2)return; // Abrupt peer loss while the page awaits its reply.
   fh::Writer reply;reply.u32(behavior==1?99u:behavior>=4?0u:2u);reply.text(behavior>=4?std::wstring(32,L'a'):L"",32);
   reply.text(behavior>=4?L"Select a saved version to preview or restore.":L"Not backed up by Fluff-History.",4096);reply.text(behavior>=4?L"This file has 20 file versions.":L"",1024);reply.u32(behavior>=4?1u:0u);reply.u32(behavior>=4?20u:0u);
   for(unsigned i=0;behavior>=4&&i<20;++i){auto key=std::wstring(30,L'b');key.push_back(L"0123456789abcdef"[i/16]);key.push_back(L"0123456789abcdef"[i%16]);reply.text(key,32);reply.text(L"sample & name.txt",1024);reply.text(L"28/10/2026 12:00",1024);reply.u64(100+i);reply.u32((i==0?1u:0u)|(i==19?2u:0u));}
   WriteFrame(pipe,stop.value,reply.frame(103));
   if(behavior==5)return;
   if(behavior==4){auto bytes=ReadFrame(pipe,stop.value,5000);fh::Reader request{bytes};auto action=request.header();fh::Check(action==6);request.text(32);auto version=request.text(32);request.u64();request.end();lastToken=version;lastAction=action;fh::Writer actionReply;actionReply.text(L"Preview opened",4096);WriteFrame(pipe,stop.value,actionReply.frame(102));}
   WaitForSingleObject(stop.value,10000);
  }catch(...) {}});return S_OK;
 });
}
bool PumpUntil(const std::function<bool()>& done,DWORD milliseconds=5000){
 auto end=GetTickCount64()+milliseconds;
 do{MSG message;while(PeekMessageW(&message,nullptr,0,0,PM_REMOVE)){TranslateMessage(&message);DispatchMessageW(&message);}if(done())return true;MsgWaitForMultipleObjects(0,nullptr,FALSE,10,QS_ALLINPUT);}while(GetTickCount64()<end);return done();
}
HWND ShowPage(const std::shared_ptr<State>& state){
 auto page=new Page{state};PROPSHEETPAGEW data{};data.dwSize=sizeof(data);data.dwFlags=PSP_USETITLE|PSP_USECALLBACK;data.hInstance=fh::module;data.pszTemplate=MAKEINTRESOURCEW(201);data.pszTitle=L"Fluff-History";data.pfnDlgProc=Dialog;data.pfnCallback=PageCallback;data.lParam=reinterpret_cast<LPARAM>(page);++fh::objects;
 auto handle=CreatePropertySheetPageW(&data);fh::Check(handle!=nullptr);PROPSHEETHEADERW header{};header.dwSize=sizeof(header);header.dwFlags=PSH_MODELESS|PSH_NOAPPLYNOW;header.hInstance=fh::module;header.pszCaption=L"Native Shell fault validation";header.nPages=1;header.phpage=&handle;
 auto window=reinterpret_cast<HWND>(PropertySheetW(&header));fh::Check(window && window!=reinterpret_cast<HWND>(-1));return window;
}
void Finish(HWND sheet){
 DestroyWindow(sheet);fh::Check(PumpUntil([](){return fh::objects==0;}));SetEvent(stop.value);
 for(auto& thread:servers)thread.join();servers.clear();CloseHandle(stop.value);stop.value=INVALID_HANDLE_VALUE;
}
std::atomic<int> lifetimeActivations{};
HRESULT NoBrokerActivation(const std::wstring&){++lifetimeActivations;return E_FAIL;}
// A single occupied worker makes "submitted but not started" deterministic. The timeout
// also makes an accidental Shell-thread wait fail instead of hanging the regression host.
struct OccupiedPool {
 fh::Handle entered{CreateEventW(nullptr,TRUE,FALSE,nullptr)},release{CreateEventW(nullptr,TRUE,FALSE,nullptr)},left{CreateEventW(nullptr,TRUE,FALSE,nullptr)};
 PTP_POOL pool{};PTP_WORK blocker{};TP_CALLBACK_ENVIRON environment{};
 static void CALLBACK Block(PTP_CALLBACK_INSTANCE,void* context,PTP_WORK){
  auto self=static_cast<OccupiedPool*>(context);SetEvent(self->entered.value);
  WaitForSingleObject(self->release.value,10000);SetEvent(self->left.value);
 }
 OccupiedPool(){
  fh::Check(entered.valid()&&release.valid()&&left.valid());
  pool=CreateThreadpool(nullptr);fh::Check(pool!=nullptr);
  InitializeThreadpoolEnvironment(&environment);SetThreadpoolCallbackPool(&environment,pool);
  try{
   SetThreadpoolThreadMaximum(pool,1);fh::Check(SetThreadpoolThreadMinimum(pool,1)!=FALSE);
   blocker=CreateThreadpoolWork(Block,this,&environment);fh::Check(blocker!=nullptr);SubmitThreadpoolWork(blocker);
   fh::Check(WaitForSingleObject(entered.value,5000)==WAIT_OBJECT_0);
  }catch(...){Close();throw;}
 }
 void Close() noexcept {
  SetEvent(release.value);
  if(blocker){WaitForThreadpoolWorkCallbacks(blocker,FALSE);CloseThreadpoolWork(blocker);blocker=nullptr;}
  DestroyThreadpoolEnvironment(&environment);if(pool){CloseThreadpool(pool);pool=nullptr;}
 }
 ~OccupiedPool(){Close();}
 OccupiedPool(const OccupiedPool&)=delete;OccupiedPool& operator=(const OccupiedPool&)=delete;
};
bool AllExpired(const std::vector<std::weak_ptr<State>>& states){return std::all_of(states.begin(),states.end(),[](const auto& state){return state.expired();});}
void WorkerLifetimeStress(){
 constexpr int rounds=16,batch=32,rapid=128;
 for(int round=0;round<rounds;++round){
  fh::Check(fh::objects==0);lifetimeActivations=0;OccupiedPool pool;
  // Positive control: explicit cancellation really can recall a queued callback in this setup.
  std::atomic<int> controlCalls{};
  auto control=CreateThreadpoolWork([](PTP_CALLBACK_INSTANCE,void* context,PTP_WORK){++*static_cast<std::atomic<int>*>(context);},&controlCalls,&pool.environment);
  fh::Check(control!=nullptr);SubmitThreadpoolWork(control);WaitForThreadpoolWorkCallbacks(control,TRUE);CloseThreadpoolWork(control);fh::Check(controlCalls==0);
  std::vector<std::weak_ptr<State>> states;states.reserve(batch);
  for(int i=0;i<batch;++i){
   auto state=std::make_shared<State>(fh::Selection{L"C:\\synthetic-not-opened.txt",0});state->activate=NoBrokerActivation;state->workerEnvironment=&pool.environment;
   states.emplace_back(state);auto sheet=ShowPage(state);DestroyWindow(sheet);
   fh::Check(state->closed && !IsWindow(sheet));state.reset();
  }
  // These are actual StartWorker submissions from actual page creation/closure, not a
  // duplicate queue implementation. Pending contexts must keep each state and count alive.
  fh::Check(WaitForSingleObject(pool.left.value,0)==WAIT_TIMEOUT);
  fh::Check(lifetimeActivations==0 && fh::objects==batch);
  fh::Check(std::none_of(states.begin(),states.end(),[](const auto& state){return state.expired();}));
  SetEvent(pool.release.value);
  fh::Check(PumpUntil([&](){return fh::objects==0&&AllExpired(states);},10000));
  fh::Check(lifetimeActivations==0); // Closed pages do not activate a broker when the callback finally runs.
 }
 // Also cover the opposite race: a default-pool callback can start before page destruction.
 std::vector<std::weak_ptr<State>> states;states.reserve(rapid);lifetimeActivations=0;
 for(int i=0;i<rapid;++i){
  auto state=std::make_shared<State>(fh::Selection{L"C:\\synthetic-not-opened.txt",0});state->activate=NoBrokerActivation;
  states.emplace_back(state);auto sheet=ShowPage(state);DestroyWindow(sheet);fh::Check(state->closed && !IsWindow(sheet));
 }
 fh::Check(PumpUntil([&](){return fh::objects==0&&AllExpired(states);},10000));
 std::cout<<"Worker lifetime: "<<rounds*batch<<" blocked-pool queued page closures + "<<rapid<<" default-pool rapid closures passed\n";
}
std::wstring Caption(HWND body,int id){wchar_t text[4096]{};GetDlgItemTextW(body,id,text,4096);return text;}
void Verify(bool condition,std::source_location location=std::source_location::current()){
 if(!condition){std::cerr<<"Native page assertion at line "<<location.line()<<" failed\n";fh::Check(false);}
}
void TableChecks(){
 stop.value=CreateEventW(nullptr,TRUE,FALSE,nullptr);mode=4;connected=0;lastAction=0;
 auto state=std::make_shared<State>(fh::Selection{L"C:\\sample & name.txt",0});state->activate=TestActivate;auto sheet=ShowPage(state);
 Verify(PumpUntil([&](){std::lock_guard lock(state->mutex);return !state->busy;}));auto body=PropSheet_GetCurrentPageHwnd(sheet);auto list=GetDlgItem(body,2103);
 Verify(ListView_GetItemCount(list)==20&&Header_GetItemCount(ListView_GetHeader(list))==2);
 Verify(Caption(body,2101)==fh::Text(TextId::Shell_IntroFile));Verify(Caption(body,2102)==L"Select a saved version to preview or restore.");
 Verify(Caption(body,2108)==L"This file has 20 file versions.");Verify(SendDlgItemMessageW(body,2109,STM_GETICON,0,0)!=0);
 RECT client{};GetClientRect(body,&client);
 for(int id:{2101,2102,2103,2104,2105,2106,2107,2108,2109}){
  RECT bounds{};GetWindowRect(GetDlgItem(body,id),&bounds);MapWindowPoints(nullptr,body,reinterpret_cast<POINT*>(&bounds),2);
  Verify(bounds.left>=0&&bounds.top>=0&&bounds.right<=client.right&&bounds.bottom<=client.bottom);
 }
 Verify(Caption(body,2106)==fh::Text(TextId::Shell_Preview));Verify(SelectedRow(body)==0);
 LVITEMW first{};first.mask=LVIF_PARAM;first.iItem=0;Verify(ListView_GetItem(list,&first)&&first.lParam==19);
 NMLISTVIEW click{};click.hdr.hwndFrom=list;click.hdr.idFrom=2103;click.hdr.code=LVN_COLUMNCLICK;click.iSubItem=1;
 SendMessageW(body,WM_NOTIFY,2103,reinterpret_cast<LPARAM>(&click));Verify(SelectedRow(body)==0);
 Verify(ListView_GetItem(list,&first)&&first.lParam==0);
 ListView_SetItemState(list,-1,0,LVIS_SELECTED);ListView_SetItemState(list,7,LVIS_SELECTED|LVIS_FOCUSED,LVIS_SELECTED|LVIS_FOCUSED);
 auto key=state->reply.rows[7].token;SendMessageW(body,WM_COMMAND,MAKEWPARAM(2106,BN_CLICKED),reinterpret_cast<LPARAM>(GetDlgItem(body,2106)));
 Verify(PumpUntil([&](){std::lock_guard lock(state->mutex);return !state->busy&&state->result==L"Preview opened";}));
 Verify(lastAction==6&&lastToken==key&&SelectedRow(body)==7&&connected==1);
 auto all=state->reply.rows;
 // Older brokers can send both marker bits for a single saved version. Latest wins.
 {std::lock_guard lock(state->mutex);state->reply.rows={all[0]};state->reply.rows[0].flags=3;}
 Render(body,state);wchar_t date[1024]{};ListView_GetItemText(list,0,1,date,1024);
 Verify(std::wstring(date).find(fh::Text(TextId::Shell_LatestTag))!=std::wstring::npos);
 Verify(std::wstring(date).find(fh::Text(TextId::Shell_OldestTag))==std::wstring::npos);
 std::cout<<"Single version: latest takes precedence over oldest, including legacy combined flags passed\n";
 for(size_t count:{5u,20u}){
  state->reply.rows.assign(all.begin(),all.begin()+count);auto start=std::chrono::steady_clock::now();
  for(int sample=0;sample<40;++sample){Render(body,state);UpdateWindow(list);}
  auto milliseconds=std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-start).count()/40;
  std::cout<<"Native table render: rows="<<count<<" samples=40 meanMs="<<milliseconds<<"\n";
 }
 Finish(sheet);
 std::cout<<"Table: 20 rows, product icon, status once, two columns, exact selection across date sorting and Preview dispatch passed\n";
 stop.value=CreateEventW(nullptr,TRUE,FALSE,nullptr);mode=5;state=std::make_shared<State>(fh::Selection{L"C:\\sample.txt",0});state->activate=TestActivate;sheet=ShowPage(state);
 Verify(PumpUntil([&](){std::lock_guard lock(state->mutex);return !state->busy&&!state->reply.restore&&state->reply.rows.size()==20;}));body=PropSheet_GetCurrentPageHwnd(sheet);
 Verify(Caption(body,2101)==fh::Text(TextId::Shell_IntroFile)&&Caption(body,2102)==L"Select a saved version to preview or restore.");
 Verify(ListView_GetItemCount(GetDlgItem(body,2103))==20);Finish(sheet);
 std::cout<<"Detached displayed table: peer closure is silent, no refresh prompt or automatic requery passed\n";
}
}
int wmain(int argc,wchar_t**argv){
 auto com=CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED);fh::module=GetModuleHandleW(nullptr);INITCOMMONCONTROLSEX controls{sizeof(controls),ICC_STANDARD_CLASSES|ICC_WIN95_CLASSES};InitCommonControlsEx(&controls);
 try{
  Verify(SUCCEEDED(com));
  if(argc==3&&std::wstring(argv[1])==L"visual"){
   ULONG ignored{};SetThreadPreferredUILanguages(MUI_RESET_FILTERS,nullptr,&ignored);std::wstring language=argv[2];language.push_back(L'\0');SetThreadPreferredUILanguages(MUI_LANGUAGE_NAME,language.c_str(),&ignored);SetThreadUILanguage(LANGIDFROMLCID(LocaleNameToLCID(argv[2],0)));
   stop.value=CreateEventW(nullptr,TRUE,FALSE,nullptr);mode=4;auto state=std::make_shared<State>(fh::Selection{L"C:\\sample.txt",0});state->activate=TestActivate;auto sheet=ShowPage(state);
   ShowWindow(sheet,SW_SHOWNORMAL);Verify(PumpUntil([&](){std::lock_guard lock(state->mutex);return !state->busy;}));std::cout<<"VISUAL_READY"<<std::endl;PumpUntil([](){return false;},6000);Finish(sheet);CoUninitialize();return 0;
  }
  for(int behavior:{1,2,3}){
   stop.value=CreateEventW(nullptr,TRUE,FALSE,nullptr);mode=behavior;connected=0;auto state=std::make_shared<State>(fh::Selection{L"C:\\synthetic-not-opened.txt",0});state->activate=TestActivate;auto sheet=ShowPage(state);
   Verify(PumpUntil([](){return connected>0;}));
   if(behavior==3){Verify(IsWindow(sheet)!=FALSE);Finish(sheet);Verify(!IsWindow(sheet));std::cout<<"Outstanding query close: passed\n";continue;}
   Verify(PumpUntil([&](){std::lock_guard lock(state->mutex);return !state->busy && !state->reply.restore && state->status==fh::TextId::Shell_UnavailableContinue;}));
   DWORD_PTR ignored{};Verify(SendMessageTimeoutW(sheet,WM_NULL,0,0,SMTO_ABORTIFHUNG,500,&ignored)!=0);
   // There is no refresh button. A new page explicitly obtains a fresh session.
   Finish(sheet);stop.value=CreateEventW(nullptr,TRUE,FALSE,nullptr);mode=0;
   state=std::make_shared<State>(fh::Selection{L"C:\\synthetic-not-opened.txt",0});state->activate=TestActivate;sheet=ShowPage(state);
   Verify(PumpUntil([&](){std::lock_guard lock(state->mutex);return !state->busy && state->reply.status==2;}));
   auto body=PropSheet_GetCurrentPageHwnd(sheet);Verify(Caption(body,2101)==fh::Text(TextId::Shell_IntroFile)&&Caption(body,2102)==L"Not backed up by Fluff-History.");
   Finish(sheet);std::cout<<(behavior==1?"Malformed response + explicit page reopen: passed\n":"Broker disconnect + explicit page reopen: passed\n");
  }
  TableChecks();WorkerLifetimeStress();
  CoUninitialize();return 0;
 }catch(...){SetEvent(stop.value);for(auto& thread:servers)if(thread.joinable())thread.join();std::cerr<<"Native Shell fault check failed\n";if(SUCCEEDED(com))CoUninitialize();return 1;}
}
