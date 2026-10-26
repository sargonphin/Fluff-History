// Shared by canonical Setup and the focused native installer acceptance fixtures.
// One fingerprint lives in Inno's existing per-user previous-installation data.
#ifndef DistributionTermsHash
 #error DistributionTermsHash is required
#endif
var
  DistributionTermsAccepted: Boolean;
  DistributionTermsArgumentInvalid: Boolean;

function RequireDistributionTerms: String;
begin
  Result := '';
  if DistributionTermsArgumentInvalid or not DistributionTermsAccepted then begin
    Result := 'Microsoft component terms require agreement. Run Setup interactively, or review DistributionTerms.txt and pass /ACCEPTDISTRIBUTABLETERMS={#DistributionTermsHash}.';
    Log(Result);
  end;
end;

function InitializeSetup: Boolean;
var ExplicitHash: String;
begin
  ExplicitHash := ExpandConstant('{param:ACCEPTDISTRIBUTABLETERMS|}');
  DistributionTermsArgumentInvalid := (ExplicitHash <> '') and
    (CompareText(ExplicitHash, '{#DistributionTermsHash}') <> 0);
  DistributionTermsAccepted := not DistributionTermsArgumentInvalid and
    ((CompareText(ExplicitHash, '{#DistributionTermsHash}') = 0) or
     (CompareText(GetPreviousData('AcceptedDistributionTermsSha256', ''), '{#DistributionTermsHash}') = 0));
  // Refuse before a silent wizard can show a license/error dialog or mutate installation.
  Result := not WizardSilent or (RequireDistributionTerms() = '');
end;

procedure InitializeWizard;
begin
  WizardForm.LicenseAcceptedRadio.Checked := DistributionTermsAccepted;
  WizardForm.LicenseNotAcceptedRadio.Checked := not DistributionTermsAccepted;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = wpLicense) and DistributionTermsAccepted;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  // Silent Next events must never manufacture interactive agreement.
  if (CurPageID = wpLicense) and not WizardSilent and
     not DistributionTermsArgumentInvalid then
    DistributionTermsAccepted := WizardForm.LicenseAcceptedRadio.Checked;
end;

procedure RegisterPreviousData(PreviousDataKey: Integer);
begin
  if not DistributionTermsAccepted or DistributionTermsArgumentInvalid then
    RaiseException('Microsoft component agreement was not established.');
  if not SetPreviousData(PreviousDataKey, 'AcceptedDistributionTermsSha256', '{#DistributionTermsHash}') then
    // Agreement already occurred before activation. A persistence failure must not
    // roll back active product files; future silent Setup will require assent again.
    Log('Could not retain component agreement; a future setup may require explicit agreement again.');
end;
