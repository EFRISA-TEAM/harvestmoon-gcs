#!/usr/bin/env python3
"""
OpenAgentic Config Injector for OpenCode
=========================================
Injects OpenAgentic provider config into your existing opencode.json
WITHOUT overwriting your other settings (providers, model, agents, etc.)

The model list is fetched LIVE from https://openagentic.id/opencode-openagentic.json
(which is auto-synced from OpenAgentic model pricing), so it always matches the
models currently active on the platform.

Usage:
  python3 inject-openagentic.py
  python3 inject-openagentic.py --api-key YOUR_KEY
  python3 inject-openagentic.py --set-default
  python3 inject-openagentic.py --list-models
  python3 inject-openagentic.py --remove

One-liner install:
  curl -fsSL https://openagentic.id/inject-openagentic.py | python3 - --api-key YOUR_KEY
"""

import json
import os
import sys
import shutil
from pathlib import Path
from datetime import datetime

try:
    from urllib.request import urlopen, Request
    from urllib.error import URLError, HTTPError
except ImportError:  # Python 2 (unlikely, but keep safe)
    from urllib2 import urlopen, Request, URLError, HTTPError

CONFIG_URL = "https://openagentic.id/opencode-openagentic.json"
PROVIDER_KEY = "openagentic"
DEFAULT_MODEL = "claude-sonnet-4.6"

# Minimal fallback used only if the live config can't be fetched (offline/blocked).
FALLBACK_PROVIDER = {
    "npm": "@ai-sdk/openai-compatible",
    "name": "OpenAgentic",
    "options": {
        "baseURL": "https://openagentic.id/api/v1",
        "apiKey": "sk-25256b80d6e9a3f6d3a0900841415d17f2ca73d6c0fda3de8645b99ffe53f877"
    },
    "models": {
        DEFAULT_MODEL: {
            "name": "Claude Sonnet 4.5",
            "attachment": True, "reasoning": True, "tool_call": True, "temperature": True,
            "modalities": {"input": ["text", "image", "pdf"], "output": ["text"]},
            "limit": {"context": 200000, "output": 64000}
        }
    }
}


# ─── Helpers ──────────────────────────────────────────────────────────────────

def fetch_provider():
    """Download the live OpenAgentic provider config (auto-synced from model pricing)."""
    req = Request(CONFIG_URL, headers={"User-Agent": "openagentic-inject/1.0"})
    try:
        with urlopen(req, timeout=20) as resp:
            data = json.loads(resp.read().decode("utf-8"))
        provider = data.get("provider", {}).get(PROVIDER_KEY)
        if provider and provider.get("models"):
            return provider, True
    except Exception:
        pass
    return json.loads(json.dumps(FALLBACK_PROVIDER)), False


def get_config_path():
    """Find opencode.json config path (cross-platform)"""
    xdg = os.environ.get("XDG_CONFIG_HOME")
    if xdg:
        return Path(xdg) / "opencode" / "opencode.json"
    if sys.platform == "darwin":
        return Path.home() / ".config" / "opencode" / "opencode.json"
    elif sys.platform == "win32":
        appdata = os.environ.get("APPDATA", str(Path.home() / "AppData" / "Roaming"))
        return Path(appdata) / "opencode" / "opencode.json"
    else:
        return Path.home() / ".config" / "opencode" / "opencode.json"


def colored(text, color):
    """Simple ANSI color"""
    colors = {"green": "32", "yellow": "33", "red": "31", "cyan": "36", "bold": "1", "dim": "2"}
    code = colors.get(color, "0")
    return "\033[{}m{}\033[0m".format(code, text)


def print_banner():
    print()
    print(colored("╔══════════════════════════════════════════════════╗", "cyan"))
    print(colored("║   OpenAgentic Config Injector for OpenCode       ║", "cyan"))
    print(colored("║   https://openagentic.id                         ║", "cyan"))
    print(colored("╚══════════════════════════════════════════════════╝", "cyan"))
    print()


def print_models(provider):
    models = provider.get("models", {})
    print(colored("  Available models ({}):".format(len(models)), "dim"))
    print(colored("  ─────────────────────", "dim"))
    for model_id, info in models.items():
        print("    \u2022 {} \u2014 {}".format(model_id, info.get('name', model_id)))
    print()


# ─── Main ─────────────────────────────────────────────────────────────────────

def main():
    import argparse
    parser = argparse.ArgumentParser(description="Inject OpenAgentic provider into OpenCode config")
    parser.add_argument("--api-key", help="Your OpenAgentic API key (from dashboard)")
    parser.add_argument("--set-default", action="store_true", help="Set default model")
    parser.add_argument("--list-models", action="store_true", help="List all available models")
    parser.add_argument("--config", help="Custom path to opencode.json")
    parser.add_argument("--remove", action="store_true", help="Remove OpenAgentic provider from config")
    args = parser.parse_args()

    print_banner()

    if args.list_models:
        provider, _ = fetch_provider()
        print_models(provider)
        return

    # Determine config path
    config_path = Path(args.config) if args.config else get_config_path()
    print("  Config: {}".format(colored(str(config_path), 'cyan')))

    # Load existing config or create new
    config = {}
    if config_path.exists():
        try:
            with open(config_path, "r", encoding="utf-8") as f:
                config = json.load(f)
            print("  Status: {}".format(colored('Existing config found', 'green')))
        except json.JSONDecodeError as e:
            print("  {}".format(colored('Warning: Config has invalid JSON: {}'.format(e), 'red')))
            print("  {}".format(colored('  Creating backup and starting fresh...', 'yellow')))
            backup = config_path.with_suffix(".json.bak.{}".format(datetime.now().strftime('%Y%m%d%H%M%S')))
            shutil.copy2(config_path, backup)
            print("  Backup: {}".format(colored(str(backup), 'dim')))
            config = {}
    else:
        print("  Status: {}".format(colored('No existing config — creating new', 'yellow')))
        config_path.parent.mkdir(parents=True, exist_ok=True)

    # Handle --remove
    if args.remove:
        if "provider" in config and PROVIDER_KEY in config["provider"]:
            del config["provider"][PROVIDER_KEY]
            if not config["provider"]:
                del config["provider"]
            if config.get("model", "").startswith(PROVIDER_KEY + "/"):
                del config["model"]
            with open(config_path, "w", encoding="utf-8") as f:
                json.dump(config, f, indent=2, ensure_ascii=False)
                f.write("\n")
            print("\n  {}".format(colored('✓ OpenAgentic provider removed', 'green')))
        else:
            print("\n  {}".format(colored('ℹ OpenAgentic provider not found in config', 'yellow')))
        return

    # Fetch live provider config (auto-synced from model pricing)
    provider_data, live = fetch_provider()
    if live:
        print("  Models: {}".format(colored(
            'fetched live ({} models)'.format(len(provider_data.get("models", {}))), 'green')))
    else:
        print("  Models: {}".format(colored(
            'WARNING: could not fetch live config — using minimal fallback', 'yellow')))

    # Backup before modifying
    if config_path.exists():
        backup = config_path.with_suffix(".json.bak.{}".format(datetime.now().strftime('%Y%m%d%H%M%S')))
        shutil.copy2(config_path, backup)
        print("  Backup: {}".format(colored(str(backup), 'dim')))

    # Ensure schema
    if "$schema" not in config:
        config["$schema"] = "https://opencode.ai/config.json"

    # Ensure provider dict exists
    if "provider" not in config:
        config["provider"] = {}

    # Handle API key
    if args.api_key:
        provider_data["options"]["apiKey"] = args.api_key
    elif PROVIDER_KEY in config["provider"]:
        existing_key = config["provider"][PROVIDER_KEY].get("options", {}).get("apiKey", "")
        if existing_key and existing_key != "YOUR_API_KEY_HERE":
            provider_data["options"]["apiKey"] = existing_key
            print("  API Key: {}".format(colored('Preserved existing key', 'green')))
        else:
            print("  API Key: {}".format(colored('YOUR_API_KEY_HERE (replace with your key)', 'yellow')))
    else:
        print()
        try:
            key = input("  Enter your API key (or press Enter to skip): ").strip()
            if key:
                provider_data["options"]["apiKey"] = key
            else:
                print("  API Key: {}".format(colored('Skipped — edit config later to add your key', 'yellow')))
        except (EOFError, KeyboardInterrupt):
            print()
            print("  API Key: {}".format(colored('Skipped', 'yellow')))

    # Check what's being updated
    if PROVIDER_KEY in config["provider"]:
        old_models = len(config["provider"][PROVIDER_KEY].get("models", {}))
        new_models = len(provider_data["models"])
        print("\n  {}".format(colored(
            'Updating OpenAgentic provider ({} -> {} models)'.format(old_models, new_models), 'green')))
    else:
        num_models = len(provider_data["models"])
        print("\n  {}".format(colored(
            '+ Adding OpenAgentic provider ({} models)'.format(num_models), 'green')))

    # Inject provider (only touches config["provider"][PROVIDER_KEY])
    config["provider"][PROVIDER_KEY] = provider_data

    # Optionally set default model
    if args.set_default:
        config["model"] = PROVIDER_KEY + "/" + DEFAULT_MODEL
        print("  {}".format(colored('✓ Default model set to {}/{}'.format(PROVIDER_KEY, DEFAULT_MODEL), 'green')))

    # Show other providers (not touched)
    other_providers = [k for k in config["provider"] if k != PROVIDER_KEY]
    if other_providers:
        print("\n  Other providers (untouched): {}".format(colored(', '.join(other_providers), 'dim')))

    # Write config
    with open(config_path, "w", encoding="utf-8") as f:
        json.dump(config, f, indent=2, ensure_ascii=False)
        f.write("\n")

    print("\n  {} Config saved to {}".format(colored('✅ Done!', 'green'), config_path))
    print()
    print("  {}".format(colored("Quick start:", "bold")))
    print("    opencode --model {}/{}".format(PROVIDER_KEY, DEFAULT_MODEL))
    print()
    print("  {}".format(colored("All models use prefix: {}/<model-id>".format(PROVIDER_KEY), "dim")))
    print("  {}".format(colored("Run with --list-models to see all available models", "dim")))
    print()


if __name__ == "__main__":
    main()
