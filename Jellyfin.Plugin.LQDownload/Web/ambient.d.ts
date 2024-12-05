declare global {
	interface Window {
		ApiClient: {
			_serverInfo?: {
				AccessToken?: string;
				[key: string]: any; // Add other properties if needed
			};
			[key: string]: any; // Add other properties if needed
		};
	}
}

export {};
