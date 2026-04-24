from os import getcwd, getenv, listdir, makedirs, name, path, rmdir
from sys import argv, exit, stdout, stderr
from typing import Any, Iterator, NoReturn, TextIO
from types import FrameType
from itertools import count
from requests import Session, Response, Timeout
from requests.structures import CaseInsensitiveDict
from concurrent.futures import ThreadPoolExecutor
from threading import Event
from hashlib import sha256
from shutil import move
from signal import signal, SIGINT, SIG_IGN
from time import perf_counter, time
NEW_LINE: str = "\n" if name != "nt" else "\r\n"
def has_ansi_support() -> bool:
    import os
    import sys
    if not sys.stdout.isatty():
        return False
    if os.name == "nt":
        return sys.getwindowsversion().major >= 10
    return True
TERMINAL_CLEAR_LINE: str = f"\r{' ' * 100} \r" if not has_ansi_support() else "\033[2K\r"
def _print(msg: str, error: bool = False) -> None:
    output: TextIO = stderr if error else stdout
    output.write(msg)
    output.flush()
def die(msg: str) -> NoReturn:
    _print(f"{msg}{NEW_LINE}", True)
    exit(-1)
def generate_website_token(user_agent: str, account_token: str) -> str:
    time_slot = int(time()) // 14400
    raw = f"{user_agent}::en-US::{account_token}::{time_slot}::5d4f7g8sd45fsd"
    return sha256(raw.encode()).hexdigest()
class Downloader:
    def __init__(
        self,
        root_dir: str,
        interactive: bool,
        max_workers: int,
        number_retries,
        timeout: float,
        chunk_size: int,
        stop_event: Event,
        session: Session,
        url: str,
        password: str | None = None,
    ) -> None:
        self._files_info: dict[str, dict[str, str]] = {}
        self._max_workers: int = max_workers
        self._number_retries: int = number_retries
        self._timeout: float = timeout
        self._interactive: bool = interactive
        self._chunk_size: int = chunk_size
        self._password: str | None = password
        self._session: Session = session
        self._stop_event: Event = stop_event
        self._root_dir: str = root_dir
        self._url: str = url
    def run(self) -> None:
        try:
            if not self._url.split("/")[-2] == "d":
                _print(f"The url probably doesn't have an id in it: {self._url}.{NEW_LINE}")
                return
            content_id: str = self._url.split("/")[-1]
        except IndexError:
            _print(f"{self._url} doesn't seem a valid url.{NEW_LINE}")
            return
        _password: str | None = sha256(self._password.encode()).hexdigest() if self._password else None
        content_dir: str = path.join(self._root_dir, content_id)
        self._build_content_tree_structure(content_dir, content_id, _password)
        if path.exists(content_dir) and not listdir(content_dir) and not self._files_info:
            _print(f"Empty directory for url: {self._url}, nothing done.{NEW_LINE}")
            self._remove_dir(content_dir)
            return
        if self._interactive:
            self._do_interactive(content_dir)
        self._threaded_downloads()
    def _get_response(self, **kwargs: Any) -> Response | None:
        for _ in range(self._number_retries):
            try:
                return self._session.get(timeout=self._timeout, **kwargs)
            except Timeout:
                continue
    def _threaded_downloads(self) -> None:
        with ThreadPoolExecutor(max_workers=self._max_workers) as executor:
            for item in self._files_info.values():
                if self._stop_event.is_set():
                    return
                executor.submit(self._download_content, item)
    @staticmethod
    def _create_dirs(dirname: str) -> None:
        makedirs(dirname, exist_ok = True)
    @staticmethod
    def _remove_dir(dirname: str) -> None:
        try:
            rmdir(dirname)
        except:
            pass
    def _download_content(self, file_info: dict[str, str]) -> None:
        filepath: str = path.join(file_info["path"], file_info["filename"])
        if self._should_skip_download(filepath):
            return
        tmp_file: str =  f"{filepath}.part"
        url: str = file_info["link"]
        headers: dict[str, str] = {}
        if path.isfile(tmp_file):
            part_size = int(path.getsize(tmp_file))
            headers = {"Range": f"bytes={part_size}-"}
        for _ in range(self._number_retries):
            try:
                part_size: int = 0
                if path.isfile(tmp_file):
                    part_size = int(path.getsize(tmp_file))
                    headers = {"Range": f"bytes={part_size}-"}
                has_size: str | None = self._perform_download(
                    file_info,
                    url,
                    tmp_file,
                    headers,
                    part_size
                )
            except Timeout:
                continue
            else:
                if has_size:
                    self._finalize_download(file_info, tmp_file, has_size)
                break
    @staticmethod
    def _should_skip_download(filepath: str) -> bool:
        if path.exists(filepath) and path.getsize(filepath) > 0:
            _print(f"{filepath} already exist, skipping.{NEW_LINE}")
            return True
        return False
    def _perform_download(
        self,
        file_info: dict[str, str],
        url: str,
        tmp_file: str,
        headers: dict[str, str],
        part_size: int,
    ) -> str | None:
        if self._stop_event.is_set():
            return
        response: Response | None = self._get_response(url=url, headers=headers, stream=True)
        if not response:
            _print(
                f"{TERMINAL_CLEAR_LINE}Couldn't download the file, failed to get a response from {url}.{NEW_LINE}"
            )
            return None
        with response:
            status_code: int = response.status_code
            if not self._is_valid_response(response.status_code, part_size):
                _print(str(self._session.headers))
                _print(
                    f"{TERMINAL_CLEAR_LINE}"
                    f"Couldn't download the file from {url}.{NEW_LINE}"
                    f"Status code: {status_code}{NEW_LINE}"
                )
                return None
            has_size: str | None = self._extract_file_size(response.headers, part_size)
            if not has_size:
                _print(
                    f"{TERMINAL_CLEAR_LINE}"
                    f"Couldn't find the file size from {url}.{NEW_LINE}"
                    f"Status code: {status_code}{NEW_LINE}"
                )
                return None
            self._write_chunks(
                response.iter_content(chunk_size=self._chunk_size),
                tmp_file,
                part_size,
                float(has_size),
                file_info["filename"]
            )
            return has_size
    @staticmethod
    def _is_valid_response(status_code: int, part_size: int) -> bool:
        if status_code in (403, 404, 405, 500):
            return False
        if part_size == 0:
            return status_code in (200, 206)
        if part_size > 0:
            return status_code == 206
        return False
    @staticmethod
    def _extract_file_size(headers: CaseInsensitiveDict[str], part_size: int) -> str | None:
        content_length: str | None = headers.get("Content-Length")
        content_range: str | None = headers.get("Content-Range")
        has_size: str | None = (
            content_length if part_size == 0
            else content_range.split("/")[-1] if content_range
            else None
        )
        return has_size
    def _write_chunks(
        self,
        chunks: Iterator[Any],
        tmp_file: str,
        part_size: int,
        total_size: float,
        filename: str
    ) -> None:
        start_time: float = perf_counter()
        with open(tmp_file, "ab") as f:
            for i, chunk in enumerate(chunks):
                if self._stop_event.is_set():
                    return
                f.write(chunk)
                self._update_progress(filename, part_size, i, chunk, total_size, start_time)
    def _update_progress(
        self,
        filename: str,
        part_size: int,
        i: int,
        chunk: bytes,
        total_size: float,
        start_time: float
    ) -> None:
        progress: float = (part_size + (i * len(chunk))) / total_size * 100
        rate: float = (i * len(chunk)) / (perf_counter() - start_time)
        unit: str = "B/s"
        if rate < 1024:
            unit = "B/s"
        elif rate < (1024 ** 2):
            rate /= 1024
            unit = "KB/s"
        elif rate < (1024 ** 3):
            rate /= (1024 ** 2)
            unit = "MB/s"
        else:
            rate /= (1024 ** 3)
            unit = "GB/s"
        _print(
            f"{TERMINAL_CLEAR_LINE}"
            f"Downloading {filename}: {part_size + i * len(chunk)} "
            f"of {int(total_size)} {round(progress, 1)}% {round(rate, 1)}{unit}"
        )
    @staticmethod
    def _finalize_download(file_info: dict[str, str], tmp_file: str, has_size: str) -> None:
        if path.getsize(tmp_file) == int(has_size):
            _print(
                f"{TERMINAL_CLEAR_LINE}"
                f"Downloading {file_info['filename']}: {path.getsize(tmp_file)} "
                f"of {has_size} Done!{NEW_LINE}"
            )
            move(tmp_file, path.join(file_info["path"], file_info["filename"]))
    def _register_file(self, file_index: count, filepath: str, file_url: str) -> None:
        self._files_info[str(next(file_index))] = {
            "path": path.dirname(filepath),
            "filename": path.basename(filepath),
            "link": file_url
        }
    @staticmethod
    def _resolve_naming_collision(
        pathing_count: dict[str, int],
        absolute_parent_dir: str,
        child_name: str,
        is_dir: bool = False,
    ) -> str:
        filepath: str = path.join(absolute_parent_dir, child_name)
        if filepath in pathing_count:
            pathing_count[filepath] += 1
        else:
            pathing_count[filepath] = 0
        if pathing_count and pathing_count[filepath] > 0 and is_dir:
            return f"{filepath}({pathing_count[filepath]})"
        if pathing_count and pathing_count[filepath] > 0:
            extension: str
            root, extension = path.splitext(filepath)
            return f"{root}({pathing_count[filepath]}){extension}"
        return filepath
    def _build_content_tree_structure(
        self,
        parent_dir: str,
        content_id: str,
        password: str | None = None,
        pathing_count: dict[str, int] | None = None,
        file_index: count = count(start=0, step=1)
    ) -> None:
        url: str = f"https://api.gofile.io/contents/{content_id}?cache=true&sortField=createTime&sortDirection=1"
        if not pathing_count:
            pathing_count = {}
        if password:
            url = f"{url}&password={password}"
        user_agent: str = str(self._session.headers.get("User-Agent", "Mozilla/5.0"))
        auth_header: str = str(self._session.headers.get("Authorization", ""))
        account_token: str = auth_header.replace("Bearer ", "") if auth_header else ""
        wt: str = generate_website_token(user_agent, account_token)
        response: Response | None = self._get_response(
            url=url,
            headers={
                "X-Website-Token": wt,
                "X-BL": "en-US"
            }
        )
        json_response: dict[str, Any] = {} if not response else response.json()
        if not json_response or json_response["status"] != "ok":
            _print(f"Failed to fetch data response from the {url}.{NEW_LINE}")
            return
        data: dict[str, Any] = json_response["data"]
        if "password" in data and "passwordStatus" in data and data["passwordStatus"] != "passwordOk":
            _print(f"Password protected link. Please provide the password.{NEW_LINE}")
            return
        if data["type"] != "folder":
            filepath: str = self._resolve_naming_collision(pathing_count, parent_dir, data["name"])
            self._register_file(file_index, filepath, data["link"])
            return
        folder_name: str = data["name"]
        absolute_path: str = self._resolve_naming_collision(pathing_count, parent_dir, folder_name)
        if path.basename(parent_dir) == content_id:
            absolute_path = parent_dir
        self._create_dirs(absolute_path)
        for child in data["children"].values():
            if child["type"] == "folder":
                self._build_content_tree_structure(absolute_path, child["id"], password, pathing_count, file_index)
            else:
                filepath: str = self._resolve_naming_collision(pathing_count, absolute_path, child["name"])
                self._register_file(file_index, filepath, child["link"])
    def _print_list_files(self) -> None:
        MAX_FILENAME_CHARACTERS: int = 100
        width: int = max(len(f"[{v}] -> ") for v in self._files_info.keys())
        for (k, v) in self._files_info.items():
            filepath: str = path.join(v["path"], v["filename"])
            filepath = f"...{filepath[-MAX_FILENAME_CHARACTERS:]}" \
                if len(filepath) > MAX_FILENAME_CHARACTERS \
                else filepath
            text: str =  f"{f'[{k}] -> '.ljust(width)}{filepath}"
            _print(f"{text}{NEW_LINE}"
                   f"{'-' * len(text)}"
                   f"{NEW_LINE}"
            )
    def _do_interactive(self, content_dir: str) -> None:
        self._print_list_files()
        input_list: set[str] = set(input(
            f"Files to download (Ex: 1 3 7) | or leave empty to download them all"
            f"{NEW_LINE}"
            f":: "
        ).split())
        input_list = set(self._files_info.keys()) if not input_list \
                     else input_list & set(self._files_info.keys())
        if not input_list:
            _print(f"Nothing done.{NEW_LINE}")
            self._remove_dir(content_dir)
            return
        keys_to_delete: list[str] = list(set(self._files_info.keys()) - set(input_list))
        for key in keys_to_delete:
            del self._files_info[key]
class Manager:
    def __init__(self, url_or_file: str, password: str | None = None) -> None:
        root_dir: str | None = getenv("GF_DOWNLOAD_DIR")
        self._max_workers: int = int(getenv("GF_MAX_CONCURRENT_DOWNLOADS", 5))
        self._number_retries: int = int(getenv("GF_MAX_RETRIES", 5))
        self._timeout: float = float(getenv("GF_TIMEOUT", 15.0))
        self._user_agent: str | None = getenv("GF_USERAGENT")
        self._interactive: bool = getenv("GF_INTERACTIVE") == "1"
        self._chunk_size: int = int(getenv("GF_CHUNK_SIZE", 2097152))
        self._password: str | None = password
        self._url_or_file: str = url_or_file
        self._session: Session = Session()
        self._stop_event: Event = Event()
        self._root_dir: str = root_dir if root_dir else getcwd()
        self._session.headers.update({
            "Accept-Encoding": "gzip",
            "User-Agent": self._user_agent if self._user_agent else "Mozilla/5.0",
            "Connection": "keep-alive",
            "Accept": "*/*",
            "Origin": "https://gofile.io",
            "Referer": "https://gofile.io/",
        })
    def _parse_url_or_file(self) -> None:
        if not (path.exists(self._url_or_file) and path.isfile(self._url_or_file)):
            downloader: Downloader = Downloader(
                self._root_dir,
                self._interactive,
                self._max_workers,
                self._number_retries,
                self._timeout,
                self._chunk_size,
                self._stop_event,
                self._session,
                self._url_or_file,
                self._password
            )
            downloader.run()
            return
        with open(self._url_or_file, "r") as f:
            lines: list[str] = f.readlines()
        max_workers: int = self._max_workers if self._max_workers <= 10 else 10
        with ThreadPoolExecutor(max_workers=max_workers) as executor:
            for line in lines:
                if self._stop_event.is_set():
                    return
                line_splitted: list[str] = line.split(" ")
                url: str = line_splitted[0].strip()
                password: str | None = self._password if self._password else line_splitted[1].strip() \
                    if len(line_splitted) > 1 else self._password
                downloader: Downloader = Downloader(
                    self._root_dir,
                    False,
                    self._max_workers,
                    self._number_retries,
                    self._timeout,
                    self._chunk_size,
                    self._stop_event,
                    self._session,
                    url,
                    password
                )
                executor.submit(downloader.run)
    def run(self) -> None:
        signal(SIGINT, self._handle_sigint)
        _print(f"Starting, please wait...{NEW_LINE}")
        self._set_account_access_token(getenv("GF_TOKEN"))
        self._parse_url_or_file()
    def _set_account_access_token(self, token: str | None = None) -> None:
        if token:
            self._session.cookies.set("Cookie", f"accountToken={token}")
            self._session.headers.update({"Authorization": f"Bearer {token}"})
            return
        response: dict[Any, Any] = {}
        user_agent: str = str(self._session.headers.get("User-Agent", "Mozilla/5.0"))
        wt: str = generate_website_token(user_agent, "")
        for _ in range(self._number_retries):
            try:
                response = self._session.post(
                    "https://api.gofile.io/accounts",
                    headers={
                        "X-Website-Token": wt,
                        "X-BL": "en-US"
                    },
                    timeout=self._timeout
                ).json()
            except Timeout:
                continue
            else:
                break
        if not response and response["status"] != "ok":
            die("Account creation failed!")
        self._session.cookies.set("Cookie", f"accountToken={response['data']['token']}")
        self._session.headers.update({"Authorization": f"Bearer {response['data']['token']}"})
    def _stop(self) -> None:
        _print(f"{TERMINAL_CLEAR_LINE}Stopping, please wait...{NEW_LINE}")
        self._stop_event.set()
    def _handle_sigint(self, _: int, __: FrameType | None) -> None:
        if not self._stop_event.is_set():
            self._stop()
            signal(SIGINT, SIG_IGN)
if __name__ == "__main__":
    url_or_file: str | None = None
    password: str | None = None
    argc: int = len(argv)
    if argc > 1:
        url_or_file = argv[1]
        if argc > 2:
            password = argv[2]
        manager: Manager = Manager(url_or_file=url_or_file, password=password)
        manager.run()
    else:
        die(f"Usage:"
            f"{NEW_LINE}"
            f"python gofile-downloader.py https://gofile.io/d/contentid"
            f"{NEW_LINE}"
            f"python gofile-downloader.py https://gofile.io/d/contentid password"
        )

