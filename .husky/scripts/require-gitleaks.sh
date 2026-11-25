#!/bin/sh
# Stopper commit/push hvis gitleaks ikke er installert, med hjelp til installasjon.
if ! command -v gitleaks >/dev/null 2>&1; then
  echo "gitleaks mangler, så hooken kan ikke sjekke for hemmeligheter." >&2
  echo "Installer: winget install --id Gitleaks.Gitleaks -e (Windows), brew install gitleaks (macOS/Linux)" >&2
  echo "eller last ned fra https://github.com/gitleaks/gitleaks/releases" >&2
  exit 1
fi
