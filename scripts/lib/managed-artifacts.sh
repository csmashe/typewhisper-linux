#!/usr/bin/env bash
# Journaled, user-local managed-artifact transactions for TypeWhisper installers.
#
# This file is a library. Callers register a complete artifact plan, then call
# ma_install or ma_remove. A plan consists of whole directories, whole files,
# and exact symbolic links. Every existing destination is validated before any
# destination changes. Publications are staged beside their destination and
# renamed into place; a durable journal completes an interrupted install/remove
# on the next invocation.

if [ -n "${TYPEWHISPER_MANAGED_ARTIFACTS_LIBRARY_LOADED:-}" ]; then
  return 0
fi
TYPEWHISPER_MANAGED_ARTIFACTS_LIBRARY_LOADED=1

MA_MANIFEST_HEADER="typewhisper-managed-artifacts-v1"
MA_JOURNAL_HEADER="typewhisper-managed-artifacts-journal-v1"

declare -ag MA_PLAN_IDS=()
declare -ag MA_PLAN_TYPES=()
declare -ag MA_PLAN_DESTINATIONS=()
declare -ag MA_PLAN_SOURCES=()
declare -ag MA_PLAN_MODES=()
declare -ag MA_PLAN_LINK_TARGETS=()
declare -ag MA_PLAN_ADOPT_KINDS=()
declare -ag MA_PLAN_ADOPT_ARGS=()
declare -Ag MA_PLAN_INDEX=()

declare -Ag MA_MANIFEST_TYPES=()
declare -Ag MA_MANIFEST_FINGERPRINTS=()
declare -Ag MA_MANIFEST_DESTINATIONS=()
declare -Ag MA_MANIFEST_AUX=()

declare -Ag MA_DESIRED_FINGERPRINTS=()
declare -Ag MA_VALIDATED_OLD_EXISTS=()
# Unrecorded destinations that failed their ownership probe. They are journaled
# as present-but-foreign so removal leaves them byte-untouched and reports them.
declare -Ag MA_REMOVE_SKIP=()
declare -ag MA_REMOVE_SKIPPED_REPORT=()
declare -Ag MA_VALIDATED_OLD_FINGERPRINTS=()
declare -Ag MA_STAGE_PATHS=()
declare -Ag MA_BACKUP_PATHS=()

declare -ag MA_JOURNAL_IDS=()
declare -Ag MA_JOURNAL_TYPES=()
declare -Ag MA_JOURNAL_OLD_EXISTS=()
declare -Ag MA_JOURNAL_OLD_FINGERPRINTS=()
declare -Ag MA_JOURNAL_NEW_FINGERPRINTS=()
declare -Ag MA_JOURNAL_DESTINATIONS=()
declare -Ag MA_JOURNAL_AUX=()
declare -Ag MA_JOURNAL_STAGES=()
declare -Ag MA_JOURNAL_BACKUPS=()

MA_APP_ID=""
MA_STATE_DIR=""
MA_MANIFEST_PATH=""
MA_PENDING_MANIFEST_PATH=""
MA_JOURNAL_PATH=""
MA_LOCK_PATH=""
MA_LOCK_FD=""
MA_JOURNAL_OPERATION=""
MA_MANIFEST_PRESENT=0
MA_INSTALL_DEST_CHANGED=0
MA_REMOVE_ANY_PRESENT=0
MA_MV_EXCHANGE=0
MA_LAST_CHANGED=0
MA_LAST_MESSAGE=""

ma_error() {
  printf 'ERROR: %s\n' "$*" >&2
  return 1
}

ma_require_command() {
  command -v "$1" >/dev/null 2>&1 || {
    ma_error "required command '$1' is not available"
    return 1
  }
}

ma_reject_unsafe_text() {
  local label="$1"
  local value="$2"

  if [ -z "$value" ]; then
    ma_error "$label must not be empty"
    return 1
  fi
  case "$value" in
    *$'\n'*|*$'\t'*)
      ma_error "$label contains a tab or newline"
      return 1
      ;;
  esac
}

ma_validate_destination() {
  local destination="$1"
  ma_reject_unsafe_text "artifact destination" "$destination" || return 1
  case "$destination" in
    /*) ;;
    *)
      ma_error "artifact destination must be absolute: $destination"
      return 1
      ;;
  esac
  case "$destination" in
    /|"${HOME:-}"|"${HOME:-}/")
      ma_error "artifact destination is unsafe: $destination"
      return 1
      ;;
  esac
}

ma_initialize() {
  if [ "$#" -ne 2 ]; then
    ma_error "ma_initialize expects APP_ID and STATE_DIR"
    return 1
  fi

  MA_APP_ID="$1"
  MA_STATE_DIR="$2"
  if [[ ! "$MA_APP_ID" =~ ^[A-Za-z0-9._-]+$ ]]; then
    ma_error "application id contains unsafe characters: $MA_APP_ID"
    return 1
  fi
  ma_validate_destination "$MA_STATE_DIR" || return 1

  local command
  for command in awk basename chmod cmp cp dirname find flock ln mkdir mktemp mv readlink sed sha256sum sort stat; do
    ma_require_command "$command" || return 1
  done
  case "$(mv --help 2>/dev/null)" in
    *--exchange*) MA_MV_EXCHANGE=1 ;;
    *) MA_MV_EXCHANGE=0 ;;
  esac

  if [ -L "$MA_STATE_DIR" ]; then
    ma_error "installer state directory is a symbolic link: $MA_STATE_DIR"
    return 1
  fi
  if [ -e "$MA_STATE_DIR" ] && [ ! -d "$MA_STATE_DIR" ]; then
    ma_error "installer state path is not a directory: $MA_STATE_DIR"
    return 1
  fi

  mkdir -p -- "$MA_STATE_DIR" || return 1
  chmod 0700 -- "$MA_STATE_DIR" || return 1
  MA_MANIFEST_PATH="$MA_STATE_DIR/installation.manifest"
  MA_PENDING_MANIFEST_PATH="$MA_STATE_DIR/pending.manifest"
  MA_JOURNAL_PATH="$MA_STATE_DIR/transaction.journal"
  MA_LOCK_PATH="$MA_STATE_DIR/transaction.lock"

  MA_PLAN_IDS=()
  MA_PLAN_TYPES=()
  MA_PLAN_DESTINATIONS=()
  MA_PLAN_SOURCES=()
  MA_PLAN_MODES=()
  MA_PLAN_LINK_TARGETS=()
  MA_PLAN_ADOPT_KINDS=()
  MA_PLAN_ADOPT_ARGS=()
  MA_PLAN_INDEX=()
  MA_LAST_CHANGED=0
  MA_LAST_MESSAGE=""
}

ma_register_directory() {
  if [ "$#" -lt 2 ] || [ "$#" -gt 3 ]; then
    ma_error "ma_register_directory expects ID DESTINATION [SOURCE]"
    return 1
  fi
  ma_register_artifact "$1" directory "$2" "${3:-}" 0755 -
}

ma_register_file() {
  if [ "$#" -lt 2 ] || [ "$#" -gt 4 ]; then
    ma_error "ma_register_file expects ID DESTINATION [SOURCE] [MODE]"
    return 1
  fi
  ma_register_artifact "$1" file "$2" "${3:-}" "${4:-0644}" -
}

ma_register_link() {
  if [ "$#" -ne 3 ]; then
    ma_error "ma_register_link expects ID DESTINATION TARGET"
    return 1
  fi
  ma_register_artifact "$1" link "$2" "" - "$3"
}

ma_register_artifact() {
  local id="$1"
  local type="$2"
  local destination="$3"
  local source="$4"
  local mode="$5"
  local link_target="$6"

  if [[ ! "$id" =~ ^[A-Za-z0-9._-]+$ ]]; then
    ma_error "artifact id contains unsafe characters: $id"
    return 1
  fi
  if [ -n "${MA_PLAN_INDEX[$id]+present}" ]; then
    ma_error "artifact id is registered more than once: $id"
    return 1
  fi
  ma_validate_destination "$destination" || return 1
  if [ "$type" = link ]; then
    ma_reject_unsafe_text "link target" "$link_target" || return 1
  elif [ -n "$source" ]; then
    ma_reject_unsafe_text "artifact source" "$source" || return 1
  fi

  local index="${#MA_PLAN_IDS[@]}"
  MA_PLAN_INDEX["$id"]="$index"
  MA_PLAN_IDS+=("$id")
  MA_PLAN_TYPES+=("$type")
  MA_PLAN_DESTINATIONS+=("$destination")
  MA_PLAN_SOURCES+=("$source")
  MA_PLAN_MODES+=("$mode")
  MA_PLAN_LINK_TARGETS+=("$link_target")
  MA_PLAN_ADOPT_KINDS+=(-)
  MA_PLAN_ADOPT_ARGS+=(-)
}

# Declares the ownership evidence that lets an unrecorded destination be adopted
# instead of refused. An app payload's bytes legitimately differ across versions,
# so for installer-owned artifacts "ours" must mean identity evidence rather than
# byte equality; without this every pre-manifest install would wedge on upgrade.
ma_register_adoption() {
  if [ "$#" -lt 2 ] || [ "$#" -gt 3 ]; then
    ma_error "ma_register_adoption expects ID KIND [ARG]"
    return 1
  fi
  local id="$1" kind="$2" arg="${3:--}" index
  if [ -z "${MA_PLAN_INDEX[$id]+present}" ]; then
    ma_error "adoption probe names an unregistered artifact: $id"
    return 1
  fi
  case "$kind" in
    payload|desktop|icon|link-into) ;;
    *)
      ma_error "unknown adoption probe kind: $kind"
      return 1
      ;;
  esac
  if [ "$arg" != - ]; then
    ma_reject_unsafe_text "adoption probe argument" "$arg" || return 1
  fi
  index="${MA_PLAN_INDEX[$id]}"
  MA_PLAN_ADOPT_KINDS["$index"]="$kind"
  MA_PLAN_ADOPT_ARGS["$index"]="$arg"
}

# Reads the first value of KEY from a desktop entry, trimming one optional
# surrounding quote pair.
ma_desktop_value() {
  local desktop="$1" key="$2" line value
  while IFS= read -r line; do
    case "$line" in
      "$key"=*)
        value="${line#"$key"=}"
        value="${value#\"}"
        value="${value%\"}"
        printf '%s\n' "$value"
        return 0
        ;;
    esac
  done <"$desktop"
  return 1
}

# A .desktop file is TypeWhisper's when it carries our window class or launches
# the executable out of the app directory.
ma_desktop_is_typewhisper() {
  local desktop="$1" app_dir="$2" exec_value
  [ -f "$desktop" ] && [ ! -L "$desktop" ] || return 1
  if grep -Eq '^StartupWMClass[[:space:]]*=[[:space:]]*typewhisper[[:space:]]*$' "$desktop"; then
    return 0
  fi
  [ "$app_dir" != - ] || return 1
  exec_value="$(ma_desktop_value "$desktop" Exec)" || return 1
  case "$exec_value" in
    "$app_dir"/*) return 0 ;;
    *) return 1 ;;
  esac
}

# Returns 0 when an unrecorded destination carries enough evidence that a prior
# TypeWhisper install put it there.
ma_adoption_evidence() {
  local kind="$1" arg="$2" type="$3" destination="$4"
  local marker desktop icon_value base resolved
  case "$kind" in
    payload)
      [ "$type" = directory ] || return 1
      for marker in typewhisper.dll typewhisper.runtimeconfig.json; do
        [ -f "$destination/$marker" ] && [ ! -L "$destination/$marker" ] || return 1
      done
      return 0
      ;;
    desktop)
      [ "$type" = file ] || return 1
      ma_desktop_is_typewhisper "$destination" "$arg"
      ;;
    icon)
      # The icon carries no marker of its own, so a TypeWhisper .desktop file
      # pointing at it is the evidence.
      [ "$type" = file ] || return 1
      desktop="$arg"
      [ "$desktop" != - ] || return 1
      ma_desktop_is_typewhisper "$desktop" - || return 1
      icon_value="$(ma_desktop_value "$desktop" Icon)" || return 1
      [ -n "$icon_value" ] || return 1
      base="$(basename -- "$destination")"
      [ "$icon_value" = "$destination" ] || [ "$icon_value" = "$base" ] \
        || [ "$icon_value" = "${base%.*}" ]
      ;;
    link-into)
      [ "$type" = link ] || return 1
      [ "$arg" != - ] || return 1
      resolved="$(readlink -- "$destination")" || return 1
      case "$resolved" in
        "$arg"/*) return 0 ;;
        *) return 1 ;;
      esac
      ;;
    *) return 1 ;;
  esac
}

ma_entry_kind() {
  local path="$1"
  if [ -L "$path" ]; then
    printf 'link\n'
  elif [ -f "$path" ]; then
    printf 'file\n'
  elif [ -d "$path" ]; then
    printf 'directory\n'
  elif [ -e "$path" ]; then
    printf 'other\n'
  else
    printf 'absent\n'
  fi
}

ma_normalize_mode() {
  local mode="$1"
  while [ "${#mode}" -gt 1 ] && [ "${mode#0}" != "$mode" ]; do
    mode="${mode#0}"
  done
  printf '%s\n' "$mode"
}

ma_hash_text() {
  printf '%s' "$1" | sha256sum | awk '{print $1}'
}

ma_file_fingerprint_for_mode() {
  local path="$1"
  local mode
  mode="$(ma_normalize_mode "$2")" || return 1
  if [ -L "$path" ] || [ ! -f "$path" ]; then
    ma_error "expected a regular file without a symbolic link: $path"
    return 1
  fi
  local digest
  digest="$(sha256sum -- "$path" | awk '{print $1}')" || return 1
  printf 'file:%s:%s\n' "$mode" "$digest"
}

ma_file_fingerprint() {
  local path="$1"
  local mode
  mode="$(stat -c '%a' -- "$path")" || return 1
  ma_file_fingerprint_for_mode "$path" "$mode"
}

ma_directory_fingerprint() {
  local root="$1"
  if [ -L "$root" ] || [ ! -d "$root" ]; then
    ma_error "expected a directory without a symbolic link: $root"
    return 1
  fi
  local inventory entries unsorted_entries
  inventory="$(mktemp "${TMPDIR:-/tmp}/typewhisper-tree.XXXXXX")" || return 1
  entries="$(mktemp "${TMPDIR:-/tmp}/typewhisper-tree-entries.XXXXXX")" || {
    rm -f -- "$inventory"
    return 1
  }
  unsorted_entries="$(mktemp "${TMPDIR:-/tmp}/typewhisper-tree-unsorted.XXXXXX")" || {
    rm -f -- "$inventory" "$entries"
    return 1
  }
  if ! find -P "$root" -mindepth 1 -print0 >"$unsorted_entries" \
    || ! sort -z "$unsorted_entries" >"$entries"; then
    rm -f -- "$inventory" "$entries" "$unsorted_entries"
    return 1
  fi
  rm -f -- "$unsorted_entries"
  local entry relative mode digest
  while IFS= read -r -d '' entry; do
    relative="${entry#"$root"/}"
    case "$relative" in
      *$'\n'*|*$'\t'*)
        rm -f -- "$inventory" "$entries"
        ma_error "artifact tree contains a path with a tab or newline: $entry"
        return 1
        ;;
    esac

    if [ -L "$entry" ]; then
      rm -f -- "$inventory" "$entries"
      ma_error "artifact tree contains a symbolic link: $entry"
      return 1
    fi
    mode="$(stat -c '%a' -- "$entry")" || {
      rm -f -- "$inventory" "$entries"
      return 1
    }
    if [ -d "$entry" ]; then
      printf 'directory\t%s\t%s\n' "$mode" "$relative" >>"$inventory"
    elif [ -f "$entry" ]; then
      digest="$(sha256sum -- "$entry" | awk '{print $1}')" || {
        rm -f -- "$inventory" "$entries"
        return 1
      }
      printf 'file\t%s\t%s\t%s\n' "$mode" "$digest" "$relative" >>"$inventory"
    else
      rm -f -- "$inventory" "$entries"
      ma_error "artifact tree contains a non-file entry: $entry"
      return 1
    fi
  done <"$entries"
  rm -f -- "$entries"

  digest="$(sha256sum -- "$inventory" | awk '{print $1}')" || {
    rm -f -- "$inventory"
    return 1
  }
  rm -f -- "$inventory"
  printf 'directory:%s\n' "$digest"
}

ma_link_fingerprint() {
  printf 'link:%s\n' "$(ma_hash_text "$1")"
}

ma_path_fingerprint() {
  local type="$1"
  local path="$2"
  case "$type" in
    directory) ma_directory_fingerprint "$path" ;;
    file) ma_file_fingerprint "$path" ;;
    link)
      if [ ! -L "$path" ]; then
        ma_error "expected a symbolic link: $path"
        return 1
      fi
      ma_link_fingerprint "$(readlink -- "$path")"
      ;;
    *)
      ma_error "unknown artifact type: $type"
      return 1
      ;;
  esac
}

ma_desired_fingerprint() {
  local index="$1"
  local type="${MA_PLAN_TYPES[$index]}"
  local source="${MA_PLAN_SOURCES[$index]}"
  case "$type" in
    directory)
      if [ -z "$source" ]; then
        ma_error "directory source is required for install: ${MA_PLAN_IDS[$index]}"
        return 1
      fi
      ma_directory_fingerprint "$source"
      ;;
    file)
      if [ -z "$source" ]; then
        ma_error "file source is required for install: ${MA_PLAN_IDS[$index]}"
        return 1
      fi
      ma_file_fingerprint_for_mode "$source" "${MA_PLAN_MODES[$index]}"
      ;;
    link) ma_link_fingerprint "${MA_PLAN_LINK_TARGETS[$index]}" ;;
  esac
}

ma_matches_fingerprint() {
  local type="$1"
  local path="$2"
  local expected="$3"
  local kind
  kind="$(ma_entry_kind "$path")" || return 1
  if [ "$kind" != "$type" ]; then
    return 1
  fi
  local actual
  actual="$(ma_path_fingerprint "$type" "$path")" || return 1
  [ "$actual" = "$expected" ]
}

ma_reset_manifest() {
  MA_MANIFEST_TYPES=()
  MA_MANIFEST_FINGERPRINTS=()
  MA_MANIFEST_DESTINATIONS=()
  MA_MANIFEST_AUX=()
  MA_MANIFEST_PRESENT=0
}

ma_load_manifest() {
  ma_reset_manifest
  if [ -L "$MA_MANIFEST_PATH" ]; then
    ma_error "installation manifest is a symbolic link: $MA_MANIFEST_PATH"
    return 1
  fi
  if [ ! -e "$MA_MANIFEST_PATH" ]; then
    return 0
  fi
  if [ ! -f "$MA_MANIFEST_PATH" ]; then
    ma_error "installation manifest is not a regular file: $MA_MANIFEST_PATH"
    return 1
  fi

  local header id type fingerprint destination aux extra
  IFS= read -r header <"$MA_MANIFEST_PATH" || true
  if [ "$header" != "$MA_MANIFEST_HEADER" ]; then
    ma_error "installation manifest has an unsupported format"
    return 1
  fi
  while IFS=$'\t' read -r id type fingerprint destination aux extra; do
    [ -n "$id" ] || continue
    if [ -n "${extra:-}" ] || [ -z "${aux:-}" ]; then
      ma_error "installation manifest has a malformed record"
      return 1
    fi
    if [ -n "${MA_MANIFEST_TYPES[$id]+present}" ]; then
      ma_error "installation manifest repeats artifact id: $id"
      return 1
    fi
    MA_MANIFEST_TYPES["$id"]="$type"
    MA_MANIFEST_FINGERPRINTS["$id"]="$fingerprint"
    MA_MANIFEST_DESTINATIONS["$id"]="$destination"
    MA_MANIFEST_AUX["$id"]="$aux"
  done < <(sed '1d' -- "$MA_MANIFEST_PATH")
  MA_MANIFEST_PRESENT=1
}

ma_validate_manifest_plan() {
  local id index expected_aux
  if [ "${#MA_MANIFEST_TYPES[@]}" -ne "${#MA_PLAN_IDS[@]}" ]; then
    ma_error "installation manifest does not match the requested artifact plan"
    return 1
  fi
  for id in "${MA_PLAN_IDS[@]}"; do
    if [ -z "${MA_MANIFEST_TYPES[$id]+present}" ]; then
      ma_error "installation manifest is missing artifact: $id"
      return 1
    fi
    index="${MA_PLAN_INDEX[$id]}"
    expected_aux="${MA_PLAN_LINK_TARGETS[$index]}"
    [ "${MA_PLAN_TYPES[$index]}" = link ] || expected_aux=-
    if [ "${MA_MANIFEST_TYPES[$id]}" != "${MA_PLAN_TYPES[$index]}" ] \
      || [ "${MA_MANIFEST_DESTINATIONS[$id]}" != "${MA_PLAN_DESTINATIONS[$index]}" ] \
      || [ "${MA_MANIFEST_AUX[$id]}" != "$expected_aux" ]; then
      ma_error "installation manifest record does not match the requested artifact: $id"
      return 1
    fi
  done
}

ma_validate_current_installation() {
  local id index type destination kind expected actual
  MA_INSTALL_DEST_CHANGED=0
  MA_VALIDATED_OLD_EXISTS=()
  MA_VALIDATED_OLD_FINGERPRINTS=()
  for id in "${MA_PLAN_IDS[@]}"; do
    index="${MA_PLAN_INDEX[$id]}"
    type="${MA_PLAN_TYPES[$index]}"
    destination="${MA_PLAN_DESTINATIONS[$index]}"
    kind="$(ma_entry_kind "$destination")" || return 1
    if [ "$kind" = absent ]; then
      MA_VALIDATED_OLD_EXISTS["$id"]=0
      MA_VALIDATED_OLD_FINGERPRINTS["$id"]=-
      MA_INSTALL_DEST_CHANGED=1
      continue
    fi
    if [ "$kind" != "$type" ]; then
      ma_error "refusing $id: destination is foreign or symlinked: $destination"
      return 1
    fi

    actual="$(ma_path_fingerprint "$type" "$destination")" || return 1
    MA_VALIDATED_OLD_EXISTS["$id"]=1
    MA_VALIDATED_OLD_FINGERPRINTS["$id"]="$actual"
    if [ "$MA_MANIFEST_PRESENT" -eq 1 ]; then
      expected="${MA_MANIFEST_FINGERPRINTS[$id]}"
      if [ "$actual" != "$expected" ]; then
        ma_error "refusing $id: recorded destination was customized: $destination"
        return 1
      fi
    else
      expected="${MA_DESIRED_FINGERPRINTS[$id]}"
      if [ "$actual" != "$expected" ] \
        && ! ma_adoption_evidence \
          "${MA_PLAN_ADOPT_KINDS[$index]}" "${MA_PLAN_ADOPT_ARGS[$index]}" \
          "$type" "$destination"; then
        ma_error "refusing $id: destination is foreign: $destination"
        return 1
      fi
    fi
    if [ "$actual" != "${MA_DESIRED_FINGERPRINTS[$id]}" ]; then
      MA_INSTALL_DEST_CHANGED=1
    fi
  done
}

ma_validate_current_removal() {
  local id index type destination kind actual expected
  MA_VALIDATED_OLD_EXISTS=()
  MA_VALIDATED_OLD_FINGERPRINTS=()
  MA_REMOVE_SKIP=()
  MA_REMOVE_SKIPPED_REPORT=()
  MA_REMOVE_ANY_PRESENT=0
  for id in "${MA_PLAN_IDS[@]}"; do
    index="${MA_PLAN_INDEX[$id]}"
    type="${MA_PLAN_TYPES[$index]}"
    destination="${MA_PLAN_DESTINATIONS[$index]}"
    kind="$(ma_entry_kind "$destination")" || return 1
    if [ "$kind" = absent ]; then
      MA_VALIDATED_OLD_EXISTS["$id"]=0
      MA_VALIDATED_OLD_FINGERPRINTS["$id"]=-
      continue
    fi
    if [ "$kind" != "$type" ]; then
      ma_error "refusing removal of $id: destination type changed: $destination"
      return 1
    fi
    actual="$(ma_path_fingerprint "$type" "$destination")" || return 1
    MA_VALIDATED_OLD_EXISTS["$id"]=1
    MA_VALIDATED_OLD_FINGERPRINTS["$id"]="$actual"
    if [ "$MA_MANIFEST_PRESENT" -eq 1 ]; then
      expected="${MA_MANIFEST_FINGERPRINTS[$id]}"
      if [ "$actual" != "$expected" ]; then
        ma_error "refusing removal of $id: recorded destination was customized: $destination"
        return 1
      fi
    # Nothing was recorded, so ownership evidence is what authorizes deletion.
    # A destination without it belongs to something else: leave it and report it,
    # rather than abandoning the artifacts that are provably ours.
    elif ! ma_adoption_evidence \
      "${MA_PLAN_ADOPT_KINDS[$index]}" "${MA_PLAN_ADOPT_ARGS[$index]}" \
      "$type" "$destination"; then
      MA_REMOVE_SKIP["$id"]=1
      MA_REMOVE_SKIPPED_REPORT+=(
        "$id: $destination carries no TypeWhisper ownership evidence"
      )
      continue
    fi
    MA_REMOVE_ANY_PRESENT=1
  done
}

ma_make_transaction_path() {
  local destination="$1"
  local label="$2"
  local directory base candidate nonce=0
  directory="$(dirname -- "$destination")" || return 1
  base="$(basename -- "$destination")" || return 1
  while :; do
    candidate="$directory/.${base}.ma-${label}.$$.$RANDOM.$nonce"
    if [ ! -e "$candidate" ] && [ ! -L "$candidate" ]; then
      printf '%s\n' "$candidate"
      return 0
    fi
    nonce=$((nonce + 1))
  done
}

ma_is_transaction_path() {
  local path="$1"
  local base
  [ -n "$path" ] || return 1
  case "$path" in
    /*) ;;
    *) return 1 ;;
  esac
  base="$(basename -- "$path")" || return 1
  case "$base" in
    .*.ma-stage.*|.*.ma-backup.*) return 0 ;;
    *) return 1 ;;
  esac
}

ma_validate_transaction_path_for_destination() {
  local path="$1"
  local destination="$2"
  local label="$3"
  local path_directory destination_directory path_base destination_base
  ma_is_transaction_path "$path" || return 1
  path_directory="$(dirname -- "$path")" || return 1
  destination_directory="$(dirname -- "$destination")" || return 1
  path_base="$(basename -- "$path")" || return 1
  destination_base="$(basename -- "$destination")" || return 1
  [ "$path_directory" = "$destination_directory" ] || return 1
  case "$path_base" in
    ".${destination_base}.ma-${label}."*) return 0 ;;
    *) return 1 ;;
  esac
}

ma_remove_transaction_path() {
  local path="$1"
  [ "$path" = - ] && return 0
  if ! ma_is_transaction_path "$path"; then
    ma_error "refusing to clean an unsafe transaction path: $path"
    return 1
  fi
  if [ -e "$path" ] || [ -L "$path" ]; then
    rm -rf -- "$path"
  fi
}

ma_stage_plan() {
  MA_STAGE_PATHS=()
  MA_BACKUP_PATHS=()
  local id index type destination source mode parent stage backup staged_fp
  for id in "${MA_PLAN_IDS[@]}"; do
    index="${MA_PLAN_INDEX[$id]}"
    type="${MA_PLAN_TYPES[$index]}"
    destination="${MA_PLAN_DESTINATIONS[$index]}"
    source="${MA_PLAN_SOURCES[$index]}"
    mode="${MA_PLAN_MODES[$index]}"
    parent="$(dirname -- "$destination")" || return 1
    mkdir -p -- "$parent" || return 1
    stage="$(ma_make_transaction_path "$destination" stage)" || return 1
    backup="$(ma_make_transaction_path "$destination" backup)" || return 1
    MA_STAGE_PATHS["$id"]="$stage"
    MA_BACKUP_PATHS["$id"]="$backup"

    case "$type" in
      directory)
        mkdir -m 0700 -- "$stage" || return 1
        if ! cp -a -- "$source/." "$stage/"; then
          ma_remove_transaction_path "$stage" || true
          return 1
        fi
        chmod "${mode#0}" -- "$stage" || return 1
        ;;
      file)
        cp -- "$source" "$stage" || return 1
        chmod "${mode#0}" -- "$stage" || return 1
        ;;
      link)
        ln -s -- "${MA_PLAN_LINK_TARGETS[$index]}" "$stage" || return 1
        ;;
    esac
    staged_fp="$(ma_path_fingerprint "$type" "$stage")" || return 1
    if [ "$staged_fp" != "${MA_DESIRED_FINGERPRINTS[$id]}" ]; then
      ma_error "staged artifact failed exact validation: $id"
      return 1
    fi
  done
}

ma_cleanup_plan_stages() {
  local id
  for id in "${MA_PLAN_IDS[@]}"; do
    if [ -n "${MA_STAGE_PATHS[$id]+present}" ]; then
      ma_remove_transaction_path "${MA_STAGE_PATHS[$id]}" || true
    fi
  done
}

ma_write_manifest_from_plan() {
  local destination_file="$1"
  local temporary="$MA_STATE_DIR/.manifest.$$.$RANDOM.tmp"
  local id index aux
  {
    printf '%s\n' "$MA_MANIFEST_HEADER"
    for id in "${MA_PLAN_IDS[@]}"; do
      index="${MA_PLAN_INDEX[$id]}"
      aux="${MA_PLAN_LINK_TARGETS[$index]}"
      [ "${MA_PLAN_TYPES[$index]}" = link ] || aux=-
      printf '%s\t%s\t%s\t%s\t%s\n' \
        "$id" \
        "${MA_PLAN_TYPES[$index]}" \
        "${MA_DESIRED_FINGERPRINTS[$id]}" \
        "${MA_PLAN_DESTINATIONS[$index]}" \
        "$aux"
    done
  } >"$temporary" || return 1
  chmod 0600 -- "$temporary" || return 1
  mv -f -- "$temporary" "$destination_file"
}

ma_write_manifest_from_journal() {
  local temporary="$MA_STATE_DIR/.manifest-recovery.$$.$RANDOM.tmp"
  local id
  {
    printf '%s\n' "$MA_MANIFEST_HEADER"
    for id in "${MA_JOURNAL_IDS[@]}"; do
      printf '%s\t%s\t%s\t%s\t%s\n' \
        "$id" \
        "${MA_JOURNAL_TYPES[$id]}" \
        "${MA_JOURNAL_NEW_FINGERPRINTS[$id]}" \
        "${MA_JOURNAL_DESTINATIONS[$id]}" \
        "${MA_JOURNAL_AUX[$id]}"
    done
  } >"$temporary" || return 1
  chmod 0600 -- "$temporary" || return 1
  mv -f -- "$temporary" "$MA_PENDING_MANIFEST_PATH"
}

ma_write_install_journal() {
  local temporary="$MA_STATE_DIR/.journal.$$.$RANDOM.tmp"
  local id index destination kind old_exists old_fingerprint current_fingerprint aux
  {
    printf '%s\tinstall\n' "$MA_JOURNAL_HEADER"
    for id in "${MA_PLAN_IDS[@]}"; do
      index="${MA_PLAN_INDEX[$id]}"
      destination="${MA_PLAN_DESTINATIONS[$index]}"
      kind="$(ma_entry_kind "$destination")" || return 1
      old_exists="${MA_VALIDATED_OLD_EXISTS[$id]}"
      old_fingerprint="${MA_VALIDATED_OLD_FINGERPRINTS[$id]}"
      if [ "$old_exists" -eq 0 ]; then
        if [ "$kind" != absent ]; then
          ma_error "destination changed after validation: $destination"
          return 1
        fi
      else
        if [ "$kind" != "${MA_PLAN_TYPES[$index]}" ]; then
          ma_error "destination type changed after validation: $destination"
          return 1
        fi
        current_fingerprint="$(ma_path_fingerprint "${MA_PLAN_TYPES[$index]}" "$destination")" \
          || return 1
        if [ "$current_fingerprint" != "$old_fingerprint" ]; then
          ma_error "destination changed after validation: $destination"
          return 1
        fi
      fi
      aux="${MA_PLAN_LINK_TARGETS[$index]}"
      [ "${MA_PLAN_TYPES[$index]}" = link ] || aux=-
      printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' \
        "$id" \
        "${MA_PLAN_TYPES[$index]}" \
        "$old_exists" \
        "$old_fingerprint" \
        "${MA_DESIRED_FINGERPRINTS[$id]}" \
        "$destination" \
        "$aux" \
        "${MA_STAGE_PATHS[$id]}" \
        "${MA_BACKUP_PATHS[$id]}"
    done
  } >"$temporary" || return 1
  chmod 0600 -- "$temporary" || return 1
  mv -f -- "$temporary" "$MA_JOURNAL_PATH"
}

ma_write_remove_journal() {
  local temporary="$MA_STATE_DIR/.journal.$$.$RANDOM.tmp"
  local id index destination kind old_exists current_fingerprint aux backup
  {
    printf '%s\tremove\n' "$MA_JOURNAL_HEADER"
    for id in "${MA_PLAN_IDS[@]}"; do
      index="${MA_PLAN_INDEX[$id]}"
      destination="${MA_PLAN_DESTINATIONS[$index]}"
      kind="$(ma_entry_kind "$destination")" || return 1
      old_exists="${MA_VALIDATED_OLD_EXISTS[$id]}"
      if [ "$old_exists" -eq 0 ]; then
        if [ "$kind" != absent ]; then
          ma_error "destination changed after removal validation: $destination"
          return 1
        fi
      else
        if [ "$kind" != "${MA_PLAN_TYPES[$index]}" ]; then
          ma_error "destination type changed after removal validation: $destination"
          return 1
        fi
        current_fingerprint="$(ma_path_fingerprint "${MA_PLAN_TYPES[$index]}" "$destination")" \
          || return 1
        if [ "$current_fingerprint" != "${MA_VALIDATED_OLD_FINGERPRINTS[$id]}" ]; then
          ma_error "destination changed after removal validation: $destination"
          return 1
        fi
      fi
      # 2 marks a destination that exists but is not ours: recorded so recovery
      # can prove it never changed, never moved to a backup.
      if [ "${MA_REMOVE_SKIP[$id]:-0}" -eq 1 ]; then
        old_exists=2
      fi
      aux="${MA_PLAN_LINK_TARGETS[$index]}"
      [ "${MA_PLAN_TYPES[$index]}" = link ] || aux=-
      backup="$(ma_make_transaction_path "$destination" backup)" || return 1
      printf '%s\t%s\t%s\t%s\t-\t%s\t%s\t-\t%s\n' \
        "$id" \
        "${MA_PLAN_TYPES[$index]}" \
        "$old_exists" \
        "${MA_VALIDATED_OLD_FINGERPRINTS[$id]}" \
        "$destination" \
        "$aux" \
        "$backup"
    done
  } >"$temporary" || return 1
  chmod 0600 -- "$temporary" || return 1
  mv -f -- "$temporary" "$MA_JOURNAL_PATH"
}

ma_reset_journal() {
  MA_JOURNAL_IDS=()
  MA_JOURNAL_TYPES=()
  MA_JOURNAL_OLD_EXISTS=()
  MA_JOURNAL_OLD_FINGERPRINTS=()
  MA_JOURNAL_NEW_FINGERPRINTS=()
  MA_JOURNAL_DESTINATIONS=()
  MA_JOURNAL_AUX=()
  MA_JOURNAL_STAGES=()
  MA_JOURNAL_BACKUPS=()
  MA_JOURNAL_OPERATION=""
}

ma_load_journal() {
  ma_reset_journal
  if [ -L "$MA_JOURNAL_PATH" ] || [ ! -f "$MA_JOURNAL_PATH" ]; then
    ma_error "transaction journal is missing, symlinked, or not a regular file"
    return 1
  fi
  local marker operation extra
  IFS=$'\t' read -r marker operation extra <"$MA_JOURNAL_PATH" || true
  if [ "$marker" != "$MA_JOURNAL_HEADER" ] \
    || { [ "$operation" != install ] && [ "$operation" != remove ]; } \
    || [ -n "${extra:-}" ]; then
    ma_error "transaction journal has an unsupported format"
    return 1
  fi
  MA_JOURNAL_OPERATION="$operation"

  local id type old_exists old_fp new_fp destination aux stage backup trailing
  while IFS=$'\t' read -r id type old_exists old_fp new_fp destination aux stage backup trailing; do
    [ -n "$id" ] || continue
    if [ -n "${trailing:-}" ] || [ -z "${backup:-}" ]; then
      ma_error "transaction journal has a malformed record"
      return 1
    fi
    if [ -n "${MA_JOURNAL_TYPES[$id]+present}" ]; then
      ma_error "transaction journal repeats artifact id: $id"
      return 1
    fi
    MA_JOURNAL_IDS+=("$id")
    MA_JOURNAL_TYPES["$id"]="$type"
    MA_JOURNAL_OLD_EXISTS["$id"]="$old_exists"
    MA_JOURNAL_OLD_FINGERPRINTS["$id"]="$old_fp"
    MA_JOURNAL_NEW_FINGERPRINTS["$id"]="$new_fp"
    MA_JOURNAL_DESTINATIONS["$id"]="$destination"
    MA_JOURNAL_AUX["$id"]="$aux"
    MA_JOURNAL_STAGES["$id"]="$stage"
    MA_JOURNAL_BACKUPS["$id"]="$backup"
  done < <(sed '1d' -- "$MA_JOURNAL_PATH")
  if [ "${#MA_JOURNAL_IDS[@]}" -eq 0 ]; then
    ma_error "transaction journal contains no artifacts"
    return 1
  fi
}

ma_validate_journal_plan() {
  if [ "${#MA_JOURNAL_IDS[@]}" -ne "${#MA_PLAN_IDS[@]}" ]; then
    ma_error "pending transaction does not match the requested artifact plan"
    return 1
  fi
  local id index aux
  for id in "${MA_JOURNAL_IDS[@]}"; do
    if [ -z "${MA_PLAN_INDEX[$id]+present}" ]; then
      ma_error "pending transaction contains an unexpected artifact: $id"
      return 1
    fi
    index="${MA_PLAN_INDEX[$id]}"
    aux="${MA_PLAN_LINK_TARGETS[$index]}"
    [ "${MA_PLAN_TYPES[$index]}" = link ] || aux=-
    if [ "${MA_JOURNAL_TYPES[$id]}" != "${MA_PLAN_TYPES[$index]}" ] \
      || [ "${MA_JOURNAL_DESTINATIONS[$id]}" != "${MA_PLAN_DESTINATIONS[$index]}" ] \
      || [ "${MA_JOURNAL_AUX[$id]}" != "$aux" ]; then
      ma_error "pending transaction does not match artifact: $id"
      return 1
    fi
    ma_validate_transaction_path_for_destination \
      "${MA_JOURNAL_BACKUPS[$id]}" \
      "${MA_JOURNAL_DESTINATIONS[$id]}" \
      backup || {
      ma_error "pending transaction has an unsafe backup path"
      return 1
    }
    if [ "${MA_JOURNAL_OPERATION}" = install ]; then
      ma_validate_transaction_path_for_destination \
        "${MA_JOURNAL_STAGES[$id]}" \
        "${MA_JOURNAL_DESTINATIONS[$id]}" \
        stage || {
        ma_error "pending transaction has an unsafe stage path"
        return 1
      }
    elif [ "${MA_JOURNAL_STAGES[$id]}" != - ]; then
      ma_error "remove journal unexpectedly contains a stage path"
      return 1
    fi
  done
}

ma_checkpoint() {
  local checkpoint="$1"
  case "${MANAGED_ARTIFACTS_TEST_HOOK:-}" in
    "kill:$checkpoint")
      kill -KILL "$$"
      ;;
    "pause:$checkpoint")
      if [ -z "${MANAGED_ARTIFACTS_TEST_READY_FILE:-}" ] \
        || [ -z "${MANAGED_ARTIFACTS_TEST_RELEASE_FILE:-}" ]; then
        ma_error "pause hook requires ready and release file variables"
        return 1
      fi
      printf '%s\n' "$checkpoint" >"$MANAGED_ARTIFACTS_TEST_READY_FILE"
      while [ ! -e "$MANAGED_ARTIFACTS_TEST_RELEASE_FILE" ]; do
        sleep 0.05
      done
      ;;
  esac
}

ma_finish_install_journal() {
  ma_load_journal || return 1
  [ "$MA_JOURNAL_OPERATION" = install ] || {
    ma_error "expected an install journal"
    return 1
  }
  ma_validate_journal_plan || return 1
  if [ ! -f "$MA_PENDING_MANIFEST_PATH" ] || [ -L "$MA_PENDING_MANIFEST_PATH" ]; then
    ma_write_manifest_from_journal || return 1
  fi

  local id type destination old_exists old_fp new_fp stage backup kind
  for id in "${MA_JOURNAL_IDS[@]}"; do
    type="${MA_JOURNAL_TYPES[$id]}"
    destination="${MA_JOURNAL_DESTINATIONS[$id]}"
    old_exists="${MA_JOURNAL_OLD_EXISTS[$id]}"
    old_fp="${MA_JOURNAL_OLD_FINGERPRINTS[$id]}"
    new_fp="${MA_JOURNAL_NEW_FINGERPRINTS[$id]}"
    stage="${MA_JOURNAL_STAGES[$id]}"
    backup="${MA_JOURNAL_BACKUPS[$id]}"
    if ma_matches_fingerprint "$type" "$destination" "$new_fp"; then
      if [ "$old_exists" -eq 1 ] && [ "$old_fp" != "$new_fp" ]; then
        if [ -e "$stage" ] || [ -L "$stage" ]; then
          if [ -e "$backup" ] || [ -L "$backup" ] \
            || ! ma_matches_fingerprint "$type" "$stage" "$old_fp"; then
            ma_error "atomically exchanged install stage or backup was modified: $destination"
            return 1
          fi
          mv -- "$stage" "$backup" || return 1
        elif [ -e "$backup" ] || [ -L "$backup" ]; then
          if ! ma_matches_fingerprint "$type" "$backup" "$old_fp"; then
            ma_error "pending install lost its exact old image: $destination"
            return 1
          fi
        fi
      else
        ma_remove_transaction_path "$stage" || return 1
      fi
      continue
    fi

    kind="$(ma_entry_kind "$destination")" || return 1
    if [ "$old_exists" -eq 1 ]; then
      if [ -e "$backup" ] || [ -L "$backup" ]; then
        if ! ma_matches_fingerprint "$type" "$backup" "$old_fp"; then
          ma_error "pending install backup was modified: $backup"
          return 1
        fi
        if [ "$kind" != absent ]; then
          ma_error "pending install destination matches neither old nor new: $destination"
          return 1
        fi
      else
        if ! ma_matches_fingerprint "$type" "$destination" "$old_fp"; then
          ma_error "pending install destination matches neither old nor new: $destination"
          return 1
        fi
        if [ "$MA_MV_EXCHANGE" -eq 1 ]; then
          if ! ma_matches_fingerprint "$type" "$stage" "$new_fp"; then
            ma_error "pending install stage is missing or modified: $stage"
            return 1
          fi
          if mv --exchange --no-copy -T -- "$stage" "$destination" 2>/dev/null; then
            if ! ma_matches_fingerprint "$type" "$destination" "$new_fp" \
              || ! ma_matches_fingerprint "$type" "$stage" "$old_fp"; then
              ma_error "atomic artifact exchange did not preserve exact old and new images: $destination"
              return 1
            fi
            ma_checkpoint "install-after-exchange-$id" || return 1
            mv -- "$stage" "$backup" || return 1
            if ! ma_matches_fingerprint "$type" "$backup" "$old_fp"; then
              ma_error "atomic exchange backup changed before it was recorded: $destination"
              return 1
            fi
            ma_checkpoint "install-after-backup-$id" || return 1
            ma_checkpoint "install-after-publish-$id" || return 1
            continue
          fi

          # Some filesystems do not implement renameat2(RENAME_EXCHANGE) even
          # when coreutils exposes --exchange. Fall back only if the failed call
          # demonstrably left both exact images unchanged.
          if ! ma_matches_fingerprint "$type" "$destination" "$old_fp" \
            || ! ma_matches_fingerprint "$type" "$stage" "$new_fp"; then
            ma_error "failed atomic exchange changed an artifact unexpectedly: $destination"
            return 1
          fi
          MA_MV_EXCHANGE=0
        fi
        mv -- "$destination" "$backup" || return 1
        if ! ma_matches_fingerprint "$type" "$backup" "$old_fp"; then
          ma_error "destination changed while it was moved to the install backup: $destination"
          return 1
        fi
        ma_checkpoint "install-after-backup-$id" || return 1
      fi
    else
      if [ "$kind" != absent ] || [ -e "$backup" ] || [ -L "$backup" ]; then
        ma_error "pending install found an unexpected destination: $destination"
        return 1
      fi
    fi

    if ! ma_matches_fingerprint "$type" "$stage" "$new_fp"; then
      ma_error "pending install stage is missing or modified: $stage"
      return 1
    fi
    mv -- "$stage" "$destination" || return 1
    ma_checkpoint "install-after-publish-$id" || return 1
  done

  mv -f -- "$MA_PENDING_MANIFEST_PATH" "$MA_MANIFEST_PATH" || return 1
  ma_checkpoint install-after-manifest || return 1
  for id in "${MA_JOURNAL_IDS[@]}"; do
    if [ "${MA_JOURNAL_OLD_EXISTS[$id]}" -eq 1 ] \
      && { [ -e "${MA_JOURNAL_BACKUPS[$id]}" ] || [ -L "${MA_JOURNAL_BACKUPS[$id]}" ]; } \
      && ! ma_matches_fingerprint \
        "${MA_JOURNAL_TYPES[$id]}" \
        "${MA_JOURNAL_BACKUPS[$id]}" \
        "${MA_JOURNAL_OLD_FINGERPRINTS[$id]}"; then
      ma_error "install backup changed before cleanup: ${MA_JOURNAL_BACKUPS[$id]}"
      return 1
    fi
    ma_remove_transaction_path "${MA_JOURNAL_BACKUPS[$id]}" || return 1
  done
  rm -f -- "$MA_JOURNAL_PATH"
}

ma_finish_remove_journal() {
  ma_load_journal || return 1
  [ "$MA_JOURNAL_OPERATION" = remove ] || {
    ma_error "expected a remove journal"
    return 1
  }
  ma_validate_journal_plan || return 1

  local id type destination old_exists old_fp backup kind
  for id in "${MA_JOURNAL_IDS[@]}"; do
    type="${MA_JOURNAL_TYPES[$id]}"
    destination="${MA_JOURNAL_DESTINATIONS[$id]}"
    old_exists="${MA_JOURNAL_OLD_EXISTS[$id]}"
    old_fp="${MA_JOURNAL_OLD_FINGERPRINTS[$id]}"
    backup="${MA_JOURNAL_BACKUPS[$id]}"
    kind="$(ma_entry_kind "$destination")" || return 1
    if [ "$old_exists" -eq 0 ]; then
      if [ "$kind" != absent ] || [ -e "$backup" ] || [ -L "$backup" ]; then
        ma_error "pending removal found an unexpected artifact: $destination"
        return 1
      fi
      continue
    fi

    if [ "$old_exists" -eq 2 ]; then
      if ! ma_matches_fingerprint "$type" "$destination" "$old_fp" \
        || [ -e "$backup" ] || [ -L "$backup" ]; then
        ma_error "skipped foreign destination changed during removal: $destination"
        return 1
      fi
      continue
    fi

    if [ -e "$backup" ] || [ -L "$backup" ]; then
      if ! ma_matches_fingerprint "$type" "$backup" "$old_fp" \
        || [ "$kind" != absent ]; then
        ma_error "pending removal backup or destination was modified: $destination"
        return 1
      fi
      continue
    fi
    if ! ma_matches_fingerprint "$type" "$destination" "$old_fp"; then
      ma_error "pending removal destination was modified: $destination"
      return 1
    fi
    mv -- "$destination" "$backup" || return 1
    if ! ma_matches_fingerprint "$type" "$backup" "$old_fp"; then
      ma_error "destination changed while it was moved to the removal backup: $destination"
      return 1
    fi
    ma_checkpoint "remove-after-backup-$id" || return 1
  done

  rm -f -- "$MA_MANIFEST_PATH" "$MA_PENDING_MANIFEST_PATH"
  ma_checkpoint remove-after-manifest || return 1
  for id in "${MA_JOURNAL_IDS[@]}"; do
    if [ "${MA_JOURNAL_OLD_EXISTS[$id]}" -eq 1 ] \
      && ! ma_matches_fingerprint \
        "${MA_JOURNAL_TYPES[$id]}" \
        "${MA_JOURNAL_BACKUPS[$id]}" \
        "${MA_JOURNAL_OLD_FINGERPRINTS[$id]}"; then
      ma_error "removal backup changed before cleanup: ${MA_JOURNAL_BACKUPS[$id]}"
      return 1
    fi
    ma_remove_transaction_path "${MA_JOURNAL_BACKUPS[$id]}" || return 1
  done
  rm -f -- "$MA_JOURNAL_PATH"
}

ma_recover_pending() {
  if [ ! -e "$MA_JOURNAL_PATH" ] && [ ! -L "$MA_JOURNAL_PATH" ]; then
    return 0
  fi
  ma_load_journal || return 1
  ma_validate_journal_plan || return 1
  if [ "$MA_JOURNAL_OPERATION" = install ]; then
    ma_finish_install_journal
  else
    ma_finish_remove_journal
  fi
}

ma_acquire_lock() {
  if [ -L "$MA_LOCK_PATH" ] || { [ -e "$MA_LOCK_PATH" ] && [ ! -f "$MA_LOCK_PATH" ]; }; then
    ma_error "installer lock path is unsafe: $MA_LOCK_PATH"
    return 1
  fi
  exec {MA_LOCK_FD}>"$MA_LOCK_PATH" || return 1
  chmod 0600 -- "$MA_LOCK_PATH" || return 1
  flock -x "$MA_LOCK_FD" || return 1
  ma_checkpoint lock-acquired
}

ma_release_lock() {
  if [ -n "$MA_LOCK_FD" ]; then
    flock -u "$MA_LOCK_FD" || true
    exec {MA_LOCK_FD}>&-
    MA_LOCK_FD=""
  fi
}

ma_install_locked() {
  if [ "${#MA_PLAN_IDS[@]}" -eq 0 ]; then
    ma_error "no managed artifacts were registered"
    return 1
  fi
  ma_recover_pending || return 1
  ma_load_manifest || return 1
  if [ "$MA_MANIFEST_PRESENT" -eq 1 ]; then
    ma_validate_manifest_plan || return 1
  fi

  MA_DESIRED_FINGERPRINTS=()
  local id index fingerprint
  for id in "${MA_PLAN_IDS[@]}"; do
    index="${MA_PLAN_INDEX[$id]}"
    fingerprint="$(ma_desired_fingerprint "$index")" || return 1
    MA_DESIRED_FINGERPRINTS["$id"]="$fingerprint"
  done
  ma_validate_current_installation || return 1
  ma_checkpoint install-after-validation || return 1
  if ! ma_stage_plan; then
    ma_cleanup_plan_stages
    return 1
  fi
  if ! ma_write_install_journal; then
    ma_cleanup_plan_stages
    return 1
  fi
  ma_checkpoint install-after-journal || return 1
  ma_write_manifest_from_plan "$MA_PENDING_MANIFEST_PATH" || return 1
  ma_checkpoint install-after-stage || return 1
  ma_finish_install_journal || return 1

  MA_LAST_CHANGED="$MA_INSTALL_DEST_CHANGED"
  MA_LAST_MESSAGE="managed artifacts installed"
}

ma_install() {
  ma_acquire_lock || return 1
  local status=0
  if ma_install_locked; then
    status=0
  else
    status=$?
  fi
  ma_release_lock
  return "$status"
}

ma_report_skipped_removals() {
  [ "${#MA_REMOVE_SKIPPED_REPORT[@]}" -gt 0 ] || return 0
  local entry
  printf 'Left in place (no TypeWhisper ownership evidence):\n'
  for entry in "${MA_REMOVE_SKIPPED_REPORT[@]}"; do
    printf '  %s\n' "$entry"
  done
}

ma_remove_locked() {
  if [ "${#MA_PLAN_IDS[@]}" -eq 0 ]; then
    ma_error "no managed artifacts were registered"
    return 1
  fi
  ma_recover_pending || return 1
  ma_load_manifest || return 1
  if [ "$MA_MANIFEST_PRESENT" -eq 1 ]; then
    ma_validate_manifest_plan || return 1
  fi
  ma_validate_current_removal || return 1
  ma_report_skipped_removals
  if [ "$MA_MANIFEST_PRESENT" -eq 0 ] && [ "$MA_REMOVE_ANY_PRESENT" -eq 0 ]; then
    MA_LAST_CHANGED=0
    if [ "${#MA_REMOVE_SKIPPED_REPORT[@]}" -gt 0 ]; then
      MA_LAST_MESSAGE="nothing TypeWhisper owns was found; \
${#MA_REMOVE_SKIPPED_REPORT[@]} left in place"
    else
      MA_LAST_MESSAGE="no recorded installation; destinations were left untouched"
    fi
    return 0
  fi
  ma_checkpoint remove-after-validation || return 1
  ma_write_remove_journal || return 1
  ma_checkpoint remove-after-journal || return 1
  ma_finish_remove_journal || return 1
  MA_LAST_CHANGED=1
  if [ "$MA_MANIFEST_PRESENT" -eq 1 ]; then
    MA_LAST_MESSAGE="recorded managed artifacts removed"
  else
    MA_LAST_MESSAGE="unrecorded managed artifacts removed on ownership evidence"
  fi
  if [ "${#MA_REMOVE_SKIPPED_REPORT[@]}" -gt 0 ]; then
    MA_LAST_MESSAGE="$MA_LAST_MESSAGE (${#MA_REMOVE_SKIPPED_REPORT[@]} left in place)"
  fi
}

ma_remove() {
  ma_acquire_lock || return 1
  local status=0
  if ma_remove_locked; then
    status=0
  else
    status=$?
  fi
  ma_release_lock
  return "$status"
}
