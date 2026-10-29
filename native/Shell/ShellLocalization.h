#pragma once
#include "ShellCommon.h"
#include "Generated/TextIds.h"

namespace fh {
inline std::wstring Text(TextId id,HINSTANCE instance=module){
 const wchar_t* value{};
 auto size=LoadStringW(instance,static_cast<UINT>(id),reinterpret_cast<LPWSTR>(&value),0);
 Check(size>0 && size<=4096 && value!=nullptr);
 return std::wstring(value,static_cast<size_t>(size));
}
inline std::wstring UiLanguage(){
 // Capture the candidate Windows actually selected. A pool thread may have another
 // preference list, and GetThreadUILanguage can filter console/complex-script locales.
 return Text(TextId::Shell_ResourceLanguage);
}
inline std::vector<uint8_t> LocalizedOpenFrame(const Selection& selection,const std::wstring& language){
 if(language.empty())return OpenFrame(selection); // Existing operation1 remains compatible.
 Writer w;w.u32(selection.kind);w.text(selection.path,32760);w.text(language,LOCALE_NAME_MAX_LENGTH);return w.frame(4);
}
inline std::vector<uint8_t> TableOpenFrame(const Selection& selection,const std::wstring& language){
 Check(!language.empty());Writer w;w.u32(selection.kind);w.text(selection.path,32760);w.text(language,LOCALE_NAME_MAX_LENGTH);return w.frame(5);
}
inline std::wstring Technical(const std::wstring& value){return L"\x2066"+value+L"\x2069";}
}
