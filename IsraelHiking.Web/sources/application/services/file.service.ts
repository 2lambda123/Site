/// <reference types="cordova" />
/// <reference types="cordova-plugin-device"/>
/// <reference types="cordova-plugin-file"/>
import { Injectable } from "@angular/core";
import { HttpClient } from "@angular/common/http";
import { Style, VectorSource, RasterDemSource, RasterSource } from "mapbox-gl";
import JSZip from "jszip";

import { ImageResizeService } from "./image-resize.service";
import { NonAngularObjectsFactory } from "./non-angular-objects.factory";
import { Urls } from "../urls";
import { DataContainer, RouteData } from "../models/models";
import { RunningContextService } from "./running-context.service";
import { SelectedRouteService } from "./layers/routelayers/selected-route.service";
import { FitBoundsService } from "./fit-bounds.service";
import { SpatialService } from "./spatial.service";
import { LoggingService } from "./logging.service";

export interface IFormatViewModel {
    label: string;
    outputFormat: string;
    extension: string;
}

@Injectable()
export class FileService {
    public formats: IFormatViewModel[];

    constructor(private readonly httpClient: HttpClient,
                private readonly runningContextService: RunningContextService,
                private readonly imageResizeService: ImageResizeService,
                private readonly nonAngularObjectsFactory: NonAngularObjectsFactory,
                private readonly selectedRouteService: SelectedRouteService,
                private readonly fitBoundsService: FitBoundsService,
                private readonly loggingService: LoggingService) {
        this.formats = [];
        this.httpClient.get(Urls.fileFormats).toPromise().then((response: IFormatViewModel[]) => {
            this.formats.splice(0);
            for (let format of response) {
                this.formats.push(format);
            }
            this.formats.push({
                label: "All routes to a single Track GPX",
                extension: "gpx",
                outputFormat: "all_gpx_single_track"
            } as IFormatViewModel);

            for (let format of this.formats) {
                format.label += ` (.${format.extension})`;
            }
        });
    }

    public getFileFromEvent(e: any): File {
        let file = e.dataTransfer ? e.dataTransfer.files[0] : e.target.files[0];
        if (!file) {
            return null;
        }
        let target = e.target || e.srcElement;
        target.value = "";
        return file;
    }

    public getFilesFromEvent(e: any): File[] {
        let files: FileList = e.dataTransfer ? e.dataTransfer.files : e.target.files;
        if (!files || files.length === 0) {
            return [];
        }
        let filesToReturn = [];
        // tslint:disable-next-line:prefer-for-of
        for (let i = 0; i < files.length; i++) {
            filesToReturn.push(files[i]);
        }
        let target = e.target || e.srcElement;
        target.value = ""; // this will reset files so we need to clone the array.
        return filesToReturn;
    }

    public getFullFilePath(relativePath: string) {
        if (!this.runningContextService.isCordova) {
            return (window.origin || window.location.origin) + "/" + relativePath;
        }
        let path = relativePath;
        if (this.runningContextService.isIos) {
            path = cordova.file.applicationDirectory + "www/" + relativePath;
            path = (window as any).Ionic.WebView.convertFileSrc(path);
        } else {
            path = "http://localhost/" + relativePath;
        }
        return path;
    }

    public getStyleJsonContent(url: string): Promise<Style> {
        if (!url.startsWith("https://") && this.runningContextService.isCordova) {
            url = (window as any).Ionic.WebView.convertFileSrc(cordova.file.dataDirectory + url);
        }
        return this.httpClient.get(url).toPromise() as Promise<Style>;
    }

    public saveToFile = async (fileName: string, format: string, dataContainer: DataContainer): Promise<boolean> => {
        let responseData = await this.httpClient.post(Urls.files + "?format=" + format, dataContainer).toPromise() as string;
        return await this.saveBytesResponseToFile(responseData, fileName);
    }

    public async addRoutesFromFile(file: File): Promise<any> {
        if (file.type === ImageResizeService.JPEG) {
            let container = await this.imageResizeService.resizeImageAndConvert(file);
            if (container.routes.length === 0 || container.routes[0].markers.length === 0) {
                throw new Error("no geographic information found in file...");
            }
            this.addRoutesFromContainer(container);
            return;
        }
        let formData = new FormData();
        formData.append("file", file, file.name);
        let fileContainer = await this.httpClient.post(Urls.openFile, formData).toPromise() as DataContainer;
        this.addRoutesFromContainer(fileContainer);
    }

    public openFromUrl = (url: string): Promise<DataContainer> => {
        return this.httpClient.get(Urls.files + "?url=" + url).toPromise() as Promise<DataContainer>;
    }

    public async addRoutesFromUrl(url: string) {
        let container = await this.openFromUrl(url);
        this.addRoutesFromContainer(container);
    }

    private addRoutesFromContainer(container: DataContainer) {
        this.selectedRouteService.addRoutes(container.routes);
        this.fitBoundsService.fitBounds(SpatialService.getBounds([container.southWest, container.northEast]));
    }

    private saveBytesResponseToFile = async (data: string, fileName: string): Promise<boolean> => {
        let blobToSave = this.nonAngularObjectsFactory.b64ToBlob(data, "application/octet-stream");
        return await this.saveAsWorkAround(blobToSave, fileName);
    }

    /**
     * This is an ugly workaround suggested here:
     * https://github.com/eligrey/FileSaver.js/issues/330
     * Plus cordova file save.
     * Return true if there's a need to show a toast message.
     * @param blob - the file to save
     * @param fileName - the file name
     */
    private saveAsWorkAround(blob: Blob, fileName: string): Promise<boolean> {
        return new Promise((resolve, reject) => {
            if (!this.runningContextService.isCordova) {
                this.nonAngularObjectsFactory.saveAsWrapper(blob, fileName, { autoBom: false });
                resolve(false);
                return;
            }
            this.getIHMDirectory().then((dir) => {
                let fullFileName = new Date().toISOString().split(":").join("-").replace("T", "_")
                    .replace("Z", "_") +
                    fileName.replace(/[/\\?%*:|"<>]/g, "-").split(" ").join("_");
                dir.getFile(fullFileName,
                    { create: true },
                    fileEntry => {
                        fileEntry.createWriter(fileWriter => {
                            fileWriter.write(blob);
                            resolve(true);
                        });
                    },
                    reject);
            }, reject);
        });
    }

    private getIHMDirectory(): Promise<DirectoryEntry> {
        return new Promise((resolve, reject) => {
            let folder = device.platform.toUpperCase().indexOf("OS") !== -1
                ? cordova.file.documentsDirectory
                : cordova.file.externalRootDirectory;
            window.resolveLocalFileSystemURL(folder,
                (directoryEntry: DirectoryEntry) => {
                    directoryEntry.getDirectory("IsraelHikingMap",
                        { create: true },
                        dir => {
                            resolve(dir);
                        }, reject);
                }, reject);
        });
    }

    public async openIHMfile(file: File,
                             tilesCallback: (address: string, content: string) => Promise<void>,
                             poisCallback: (content: string) => Promise<void>,
                             imagesCallback: (content: string) => Promise<void>,
                             notificationCallback: (message: string) => void
    ): Promise<any> {
        let zip = new JSZip();
        await zip.loadAsync(file);
        let styles = Object.keys(zip.files).filter(name => name.startsWith("styles/") && name.endsWith(".json"));
        for (let styleFileName of styles) {
            let styleText = (await zip.file(styleFileName).async("text")).trim();
            this.saveStyleJson(styleFileName.replace("styles/", ""), styleText);
        }
        let sources = Object.keys(zip.files).filter(name => name.startsWith("sources/") && name.endsWith(".json"));
        for (let sourceFileIndex = 0; sourceFileIndex < sources.length; sourceFileIndex++) {
            let sourceFile = sources[sourceFileIndex];
            let sourceName = sourceFile.split("/")[1];
            this.loggingService.debug("Adding: " + sourceFile);
            notificationCallback(`${sourceFileIndex + 1}/${sources.length}`);
            await tilesCallback(sourceName, await zip.file(sourceFile).async("text") as string);
            this.loggingService.debug("Added: " + sourceFile);
        }
        let poisFileName = Object.keys(zip.files).find(name => name.startsWith("pois/") && name.endsWith(".geojson"));
        if (poisFileName != null) {
            let poisText = (await zip.file(poisFileName).async("text")).trim();
            await poisCallback(poisText);
            this.loggingService.debug("Added pois.");
        }
        let images = Object.keys(zip.files).filter(name => name.startsWith("images/") && name.endsWith(".json"));
        for (let imagesFileIndex = 0; imagesFileIndex < images.length; imagesFileIndex++) {
            let imagesFile = images[imagesFileIndex];
            this.loggingService.debug("Adding images: " + imagesFile);
            notificationCallback(`${imagesFileIndex + 1}/${images.length}`);
            await imagesCallback(await zip.file(imagesFile).async("text") as string);
            this.loggingService.debug("Added images: " + imagesFile);
        }
    }

    private saveStyleJson(styleFileName: string, styleJsonText: string) {
        if (!this.runningContextService.isCordova) {
            return;
        }
        window.resolveLocalFileSystemURL(cordova.file.dataDirectory,
            (directoryEntry: DirectoryEntry) => {
                directoryEntry.getFile(styleFileName,
                    { create: true },
                    fileEntry => {
                        fileEntry.createWriter(fileWriter => {
                            fileWriter.onwriteend = () => {
                                fileWriter.seek(0);
                                fileWriter.onwriteend = () => {
                                    this.loggingService.info("Style Json File was written!");
                                };
                                fileWriter.write(styleJsonText as any);
                            };
                            fileWriter.truncate(0);
                        });
                    }, (err) => this.loggingService.error("File: " + err.code.toString()));
            }, (err) => this.loggingService.error("Folder: " + err.code.toString()));
    }
}
