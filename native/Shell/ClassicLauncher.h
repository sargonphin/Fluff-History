#pragma once
#include "ShellCommon.h"
#include <shellapi.h>

namespace fh {
inline bool ClassicArgumentSyntax(const std::wstring& raw) {
 if(raw.empty()||raw.size()>32766)return false;
 auto first=raw.find_first_not_of(L" \t"),last=raw.find_last_not_of(L" \t");
 if(first==std::wstring::npos)return false;
 auto value=raw.substr(first,last-first+1);
 if(value.front()==L'"')return value.size()>2 && value.back()==L'"' && value.find(L'"',1)==value.size()-1;
 return value.find_first_of(L"\" \t")==std::wstring::npos;
}
// Inspect lexical components before touching the filesystem. Never canonicalize
// untrusted input into a different item, or traverse a reparse ancestor.
inline Selection ClassicSelection(int argc, const wchar_t* const* argv) {
 Check(argc==2 && argv && argv[1]);
 std::wstring path(argv[1]);Check(!path.empty() && path.size()<=32760);
 Check(WideCharToMultiByte(CP_UTF8,WC_ERR_INVALID_CHARS,path.data(),static_cast<int>(path.size()),nullptr,0,nullptr,nullptr)>0);
 bool extended=path.starts_with(L"\\\\?\\");
 for(size_t i=0;i<path.size();++i){auto c=path[i];Check(c>=32 && c!=L'"' && c!=L'/' && c!=L'*' && (c!=L'?' || (extended&&i==2)) && c!=L'<' && c!=L'>' && c!=L'|');}
 size_t root{};
 if(extended){
  if(path.size()>7 && ((path[4]>=L'A'&&path[4]<=L'Z')||(path[4]>=L'a'&&path[4]<=L'z')) && path[5]==L':' && path[6]==L'\\')root=7;
  else if(path.starts_with(L"\\\\?\\UNC\\")){
   auto server=path.find(L'\\',8);Check(server!=std::wstring::npos && server>8);
   auto share=path.find(L'\\',server+1);Check(share!=std::wstring::npos && share>server+1);root=share+1;
  }
 }
 else if(path.size()>=3 && ((path[0]>=L'A'&&path[0]<=L'Z')||(path[0]>=L'a'&&path[0]<=L'z')) && path[1]==L':' && path[2]==L'\\')root=3;
 else if(path.starts_with(L"\\\\") && path.size()>2 && path[2]!=L'.') {
  auto server=path.find(L'\\',2);Check(server!=std::wstring::npos && server>2);
  auto share=path.find(L'\\',server+1);Check(share!=std::wstring::npos && share>server+1);root=share+1;
 }
 Check(root>0 && path.size()>root);
 size_t start=root;
 while(start<path.size()) {
  auto end=path.find(L'\\',start);if(end==std::wstring::npos)end=path.size();
  auto part=path.substr(start,end-start);
  Check(!part.empty() && part!=L"." && part!=L".." && part.find(L':')==std::wstring::npos);
  // Ordinary Win32 paths must not silently trim into another item.
  Check(extended || (part.back()!=L'.' && part.back()!=L' '));
  if(!extended){
   auto name=part.substr(0,part.find(L'.'));for(auto& c:name)if(c>=L'a'&&c<=L'z')c-=32;
   Check(name!=L"CON"&&name!=L"PRN"&&name!=L"AUX"&&name!=L"NUL"&&name!=L"CONIN$"&&name!=L"CONOUT$");
   if(name.size()==4 && (name.starts_with(L"COM")||name.starts_with(L"LPT")))Check(!((name[3]>=L'1'&&name[3]<=L'9')||name[3]==0xb9||name[3]==0xb2||name[3]==0xb3));
  }
  start=end+1;
 }
 Check(path.back()!=L'\\');
 for(size_t end=root-1;;) {
  auto current=path.substr(0,end);if(end==root-1 && (root==3 || (extended&&root==7)))current+=L'\\';
  // The long-path policy need not be changed machine-wide. Lexically validated
  // absolute long paths can be inspected verbatim through the extended API form.
  auto query=current;
  if(!extended && query.size()>=MAX_PATH)query=root==3?L"\\\\?\\"+query:L"\\\\?\\UNC\\"+query.substr(2);
  auto attributes=GetFileAttributesW(query.c_str());
  Check(attributes!=INVALID_FILE_ATTRIBUTES && !(attributes&FILE_ATTRIBUTE_REPARSE_POINT));
  if(end==path.size())return {path,(attributes&FILE_ATTRIBUTE_DIRECTORY)?1u:0u};
  Check((attributes&FILE_ATTRIBUTE_DIRECTORY)!=0);
  end=path.find(L'\\',end+1);if(end==std::wstring::npos)end=path.size();
 }
}
inline int ClassicLaunch(int argc,const wchar_t* const* argv) noexcept {
 auto result=Boundary([&](){return Browse(ClassicSelection(argc,argv));});
 return SUCCEEDED(result)?0:1;
}
}
