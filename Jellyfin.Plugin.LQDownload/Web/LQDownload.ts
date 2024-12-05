interface TranscodeItem {
	/** Video ID */
	id: string;

	/**
	 * Whether the video needs transcoding. `true` could mean
	 * it is currently transcoding. `false` could mean it's
	 * already completed.
	 */
	needsTranscoding: boolean;

	/** Actively transcoding, or in the queue */
	isTranscoding: boolean;

	/** Current progress, or pending if `0` and `isTranscoding` */
	transcodeProgress: number;
}

let configResolution = "";
let currentUrl = "";
let pageLoadTime = 0;
let currentItem: TranscodeItem | null = null;

// Initial load
onPageChange();

// Save the original methods
const originalPushState = history.pushState;
const originalReplaceState = history.replaceState;

// Override `pushState`
history.pushState = function (...args) {
	const result = originalPushState.apply(this, args);
	onPageChange();
	return result;
};

// Override `replaceState`
history.replaceState = function (...args) {
	const result = originalReplaceState.apply(this, args);
	onPageChange();
	return result;
};

// Listen for browser navigation
window.addEventListener("popstate", onPageChange);

// Listen for app command events
document.addEventListener("command", (e) => {
	if (e.target instanceof HTMLElement && e.target.classList.contains("btnMoreCommands")) onMoreButtonClick();
});

function getItemIdFromUrl() {
	const pseudoUrl = new URL(new URL(currentUrl).hash.replace("#", location.origin));
	return pseudoUrl.searchParams.get("id");
}

/**
 * When page changes
 */
function onPageChange() {
	if (location.href == currentUrl) return;
	currentUrl = location.href;
	pageLoadTime = Date.now();

	console.log("LQDOWNLOAD: page change", currentUrl);

	// Reset item
	currentItem = null;

	// Stop if not on details page
	if (!currentUrl.includes("/#/details")) return;

	// Get item ID from URL
	const itemId = getItemIdFromUrl();
	if (itemId) {
		// Create empty item
		currentItem = {
			id: itemId,
			needsTranscoding: false,
			isTranscoding: false,
			transcodeProgress: 0,
		};
		getItemTranscodeStatus();
	}
}

/**
 * Load values for item
 */
async function getItemTranscodeStatus() {
	if (currentItem == null) return;
	console.log("LQDOWNLOAD: getItemTranscodeStatus for itemId", currentItem.id);

	const urlBase = location.href.split("web/#")[0];
	const url = `${urlBase}LQDownload/ClientScript/TranscodeStatus?itemId=${encodeURIComponent(currentItem.id)}`;

	try {
		const response = await fetch(url, {
			method: "GET",
			headers: {
				"Content-Type": "application/json",
			},
		});

		if (!response.ok) {
			console.error("LQDOWNLOAD: Failed to fetch transcode status. HTTP status:", response.status);
			return;
		}

		// Check that we're still on this item's page
		const urlItemId = getItemIdFromUrl();
		if (urlItemId != currentItem?.id) return;

		const result = await response.json();
		console.log("LQDOWNLOAD: Transcode status result:", result);

		configResolution = result.configResolution;
		currentItem = result.item;

		updateUI();
	} catch (error) {
		console.error("LQDOWNLOAD: Error fetching transcode status:", error);
	}
}

/**
 * Update the UI with progress or complete
 */
function updateUI() {
	if (currentItem == null) return;

	if (currentItem.isTranscoding) {
		// Pending or currently transcoding

		const selectVideoContainers = document.querySelectorAll("form.trackSelections .selectVideoContainer");

		if (selectVideoContainers.length == 0) {
			if (Date.now() - pageLoadTime < 10000) {
				// Retry main function at increments
				setTimeout(updateUI, 500);
			} else {
				console.warn("LQDOWNLOAD: selectVideoContainers not found within 10 seconds.");
			}

			// Stop further execution
			return;
		}

		// Add progress elements before each selectVideoContainer
		selectVideoContainers.forEach((container) => {
			if (!container.previousElementSibling?.classList.contains("lqdownload-transcoding-progress")) {
				const progressEl = createTranscodeProgressElement();
				if (progressEl) container.insertAdjacentElement("beforebegin", progressEl.cloneNode(true) as Element);
			}
		});

		console.log("LQDOWNLOAD: set progress");

		// Set the progress
		document.querySelectorAll(".lqdownload-transcoding-progress").forEach((el) => {
			if (currentItem == null) return;
			const progressBar = el.querySelector(".lqdownload-progress-bar");
			if (progressBar instanceof HTMLElement) progressBar.style.width = `${currentItem.transcodeProgress}%`;
			const progressPercent = el.querySelector(".lqdownload-progress-percent");
			if (progressPercent) progressPercent.innerHTML = currentItem.transcodeProgress == 0 ? "Pending" : `${currentItem.transcodeProgress.toFixed(1)}%`;
		});

		// Refresh progress in 2 sec
		setTimeout(() => getItemTranscodeStatus(), 2000);
	} else {
		console.log("Transcode not needed, or completed");
		// Potentially completed transcode
		// Update the UI to show completed and refresh button, if progress item exists

		document.querySelectorAll(".lqdownload-transcoding-progress").forEach((el) => {
			// Remove percent
			el.querySelector(".lqdownload-progress-percent")?.remove();

			// Create button and replace progress bar
			const button = document.createElement("button");
			button.type = "button";
			button.style.appearance = "none";
			button.style.border = "none";
			button.style.display = "block";
			button.style.width = "100%";
			button.style.height = "100%";
			button.style.backgroundColor = "#007ea8";
			button.style.color = "#fff";
			button.style.cursor = "pointer";
			button.textContent = "Refresh Page";
			el.querySelector(".lqdownload-progress-bar")?.replaceWith(button);

			// Handle button clicks (refresh page)
			button.addEventListener("click", () => location.reload());
		});
	}
}

/**
 * When the More button is clicked on movie/show details page
 */
function onMoreButtonClick() {
	const timeout = 1000; // 1 second timeout
	const startTime = performance.now();

	function waitForDownloadButton() {
		const dialogs = document.querySelectorAll(".dialogContainer .dialog.opened");
		const dialog = dialogs.length ? dialogs[dialogs.length - 1] : null;

		if (dialog instanceof HTMLElement) {
			const downloadButton = dialog.querySelector('button[data-id="download"]');
			if (downloadButton instanceof HTMLElement) {
				// This is a downloadable item.
				// Check for pre - transcoded file and display new download button
				addTranscodeButton(dialog, downloadButton);
				return;
			}
		}

		// Retry on the next frame
		if (performance.now() - startTime < timeout) requestAnimationFrame(waitForDownloadButton);
	}

	requestAnimationFrame(waitForDownloadButton);
}

/**
 * Adds transcode button to the menu, if applicable
 */
async function addTranscodeButton(dialog: HTMLElement, downloadButton: HTMLElement) {
	// If it doesn't need transcoding, or it already is transcoding, don't add button
	if (!currentItem?.needsTranscoding || currentItem?.isTranscoding) return;

	// Right-align popup
	dialog.style.left = "unset";
	dialog.style.right = "10px";

	// Create transcode button
	const newButton = document.createElement("button");
	newButton.setAttribute("is", "emby-button");
	newButton.setAttribute("type", "button");
	newButton.className = "listItem listItem-button actionSheetMenuItem emby-button";
	newButton.setAttribute("data-id", "download-lq");

	// Create inner content for the button
	newButton.innerHTML = `
		<span class="actionsheetMenuItemIcon listItemIcon listItemIcon-transparent material-icons video_settings" aria-hidden="true"></span>
		<div class="listItemBody actionsheetListItemBody">
				<div class="listItemBodyText actionSheetItemText">Create ${configResolution} Version</div>
		</div>
	`;

	// Add the button click event listener
	newButton.addEventListener("click", onTranscode);

	// Insert the new button after the downloadButton in the DOM
	downloadButton.insertAdjacentElement("afterend", newButton);
}

/**
 * Transcode button click
 */
async function onTranscode() {
	// Get item ID from URL
	if (currentItem == null) return;

	console.log("LQDOWNLOAD: transcode itemId", currentItem.id);

	currentItem.needsTranscoding = false;

	const urlBase = location.href.split("web/#")[0];
	const url = `${urlBase}LQDownload/ClientScript/Transcode?itemId=${encodeURIComponent(currentItem.id)}`;

	try {
		const accessToken = getAccessToken();
		if (!accessToken) throw "No access token";

		const response = await fetch(url, {
			method: "GET",
			headers: {
				"Content-Type": "application/json",
				Authorization: `MediaBrowser Token="${accessToken}"`,
			},
		});

		if (!response.ok) {
			console.error("LQDOWNLOAD: Failed to start transcode. HTTP status:", response.status);
			return;
		}

		const result = await response.json();
		console.log("LQDOWNLOAD: Transcode start result:", result);

		currentItem.isTranscoding = true;
		currentItem.transcodeProgress = 0;

		updateUI();
	} catch (error) {
		console.error("LQDOWNLOAD: Error fetching transcode status:", error);
	}
}

/**
 * Creates an html element for transcode progress
 */
function createTranscodeProgressElement() {
	const tempContainer = document.createElement("div");
	tempContainer.innerHTML = `<div class="lqdownload-transcoding-progress" style="margin: 0 0 0.3em; display: flex; max-width: 44em">
			<div style="line-height: 1.75; margin: 0 0.2em 0 0; flex-basis: 6.25em; flex-grow: 0; flex-shrink: 0">Transcoding</div>
			<div style="position: relative; width: 100%; border-radius: 0.2em; overflow: hidden; background: #292929">
				<div class="lqdownload-progress-bar" style="width: 0; height: 100%; background: #007ea8; transition: width 2s"></div>
				<div class="lqdownload-progress-percent" style="position: absolute; top: 0; left: 0; right: 0; bottom: 0; display: flex; justify-content: center; align-items: center">0%</div>
			</div>
		</div>`;
	return tempContainer.firstElementChild;
}

/**
 * Gets an access token for authenticated API request.
 */
function getAccessToken() {
	if (typeof window.ApiClient?._serverInfo?.AccessToken === "string") return window.ApiClient._serverInfo.AccessToken;

	const jellyfinCredentials = localStorage.getItem("jellyfin_credentials");
	if (!jellyfinCredentials) return;
	const credentials = JSON.parse(jellyfinCredentials);
	if (!credentials || !Array.isArray(credentials.Servers)) return;

	// Match the server based on the current address
	const urlBase = new URL(location.href).origin;
	const server = credentials.Servers.find((s: any) => s.ManualAddress === urlBase || s.LocalAddress === urlBase);

	return server?.AccessToken;
}
